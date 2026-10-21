#!/usr/bin/env python3
"""Black-box acceptance oracle for the background jobs library.

Controls the demo only through docker compose (up, kill -9, graceful stop,
restart) and observes behavior only through the HTTP API defined by the
specification. Never inspects the database or internal classes.

Usage:  python acceptance/run.py [--keep] [--port N] [--only N[,N...]]

Prints PASS/FAIL per scenario plus the evidence used for each assertion.
Exit code 0 when every scenario passes; 1 otherwise; 2 when blocked by the
environment (daemon down, missing compose file, implementation absent).
"""

import argparse
import json
import os
import shutil
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timedelta, timezone

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
COMPOSE_FILE = os.path.join(ROOT, "docker-compose.yml")
TMP_ROOT = os.path.join(ROOT, "acceptance", ".tmp")
PROJECT = "bgacc"
DEMO_SERVICE = "demo"
DB_SERVICE = "postgres"

# Tolerances: clocks involved are the oracle host, the app and Postgres,
# all in the same local environment. Epsilon covers ms serialization only.
EPS = 0.08
START_SLA_S = 2.0
API_TIMEOUT_S = 90


# ---------------------------------------------------------------- primitives


def utcnow():
    return datetime.now(timezone.utc)


def iso(dt):
    return dt.strftime("%Y-%m-%dT%H:%M:%S.") + f"{dt.microsecond // 1000:03d}Z"


def parse_at(s):
    return datetime.fromisoformat(s.replace("Z", "+00:00"))


class ApiError(Exception):
    pass


class Api:
    """Synchronous HTTP client over urllib. Every call returns (status, body);

    body is parsed JSON when possible, else raw text. Never raises on HTTP
    error statuses — callers assert on them."""

    def __init__(self, base):
        self.base = base.rstrip("/")

    def request(self, method, path, body=None, raw=None):
        data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
        req = urllib.request.Request(
            self.base + path,
            data=data,
            method=method,
            headers={"Content-Type": "application/json"},
        )
        try:
            with urllib.request.urlopen(req, timeout=10) as res:
                payload = res.read().decode()
                return res.status, self._parse(payload)
        except urllib.error.HTTPError as e:
            payload = e.read().decode()
            return e.code, self._parse(payload)
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            raise ApiError(str(e))

    @staticmethod
    def _parse(payload):
        try:
            return json.loads(payload)
        except (json.JSONDecodeError, TypeError):
            return payload

    def create(self, job, params, options=None):
        body = {"job": job, "params": params}
        if options is not None:
            body["options"] = options
        return self.request("POST", "/demo/jobs", body)

    def jobs(self):
        return self.request("GET", "/api/jobs")

    def job(self, job_id):
        return self.request("GET", f"/api/jobs/{job_id}")

    def delete(self, job_id):
        return self.request("DELETE", f"/api/jobs/{job_id}")

    def requeue(self, job_id):
        return self.request("POST", f"/api/jobs/{job_id}/requeue")

    def events(self):
        return self.request("GET", "/demo/events")

    def health(self):
        return self.request("GET", "/health")


def wait_until(fn, timeout, interval=0.15, desc="condition"):
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        try:
            last = fn()
            if last:
                return last
        except ApiError as e:
            last = e
        time.sleep(interval)
    raise AssertionError(f"timeout waiting for {desc}; last={last!r}")


# ------------------------------------------------------------ compose driver


class Compose:
    """One demo + one postgres per run; DATA_DIR bind mount gives fresh or

    preserved storage per scenario. `env` feeds WORKER_COUNT and friends."""

    def __init__(self, data_dir, host_port):
        self.data_dir = data_dir
        self.host_port = host_port

    def _cmd(self, *args, env=None, check=True):
        e = dict(os.environ)
        e["COMPOSE_PROJECT_NAME"] = PROJECT
        e["DATA_DIR"] = self.data_dir
        e["DEMO_PORT"] = str(self.host_port)
        if env:
            e.update({k: str(v) for k, v in env.items()})
        proc = subprocess.run(
            ["docker", "compose", "-f", COMPOSE_FILE, *args],
            cwd=ROOT, env=e, capture_output=True, text=True, timeout=300,
        )
        if check and proc.returncode != 0:
            raise RuntimeError(
                f"docker compose {' '.join(args)} failed\n"
                f"stdout: {proc.stdout}\nstderr: {proc.stderr}")
        return proc

    def build(self):
        self._cmd("build", DEMO_SERVICE)

    def up(self, worker_count=4):
        self._cmd("up", "-d", env={"WORKER_COUNT": worker_count})

    def down(self):
        self._cmd("down", "--remove-orphans", check=False)

    def kill_demo(self):
        """SIGKILL: abrupt termination (kill -9 semantics)."""
        self._cmd("kill", "-s", "KILL", DEMO_SERVICE)

    def stop_demo(self, timeout_s=60):
        """SIGTERM + wait: graceful shutdown. Blocks until the demo exits."""
        self._cmd("stop", "--timeout", str(timeout_s), DEMO_SERVICE)

    def stop_demo_async(self, timeout_s=60):
        t = threading.Thread(target=self.stop_demo, args=(timeout_s,), daemon=True)
        t.start()
        return t

    def start_demo(self):
        self._cmd("start", DEMO_SERVICE)


def wait_health(api, timeout=API_TIMEOUT_S):
    return wait_until(
        lambda: api.health() == (200, {"status": "ok"}),
        timeout, desc="GET /health -> 200 ok")


def wait_state(api, job_id, state, timeout=60):
    def probe():
        status, body = api.job(job_id)
        return body if status == 200 and body.get("state") == state else None
    return wait_until(probe, timeout, desc=f"job {job_id} -> {state}")


def get_events(api, job_id=None, etype=None):
    status, body = api.events()
    assert status == 200, f"GET /demo/events -> {status}"
    items = body["items"]
    return [e for e in items
            if (job_id is None or e.get("jobId") == job_id)
            and (etype is None or e.get("type") == etype)]


# ------------------------------------------------------------------- harness


RESULTS = []


def record(name, ok, evidence):
    RESULTS.append((name, ok, evidence))
    tag = "PASS" if ok else "FAIL"
    print(f"[{tag}] {name}")
    for line in evidence:
        print(f"      {line}")


def scenario(name):
    def deco(fn):
        def wrapped(ctx, workers=4):
            env = ctx.fresh_env(workers)
            try:
                evidence = fn(env)
                record(name, True, evidence)
            except Exception as e:  # noqa: BLE001 - report any failure as FAIL
                record(name, False, [f"{type(e).__name__}: {e}"])
            finally:
                if not ctx.keep:
                    env.compose.down()
        return wrapped
    return deco


class Ctx:
    def __init__(self, host_port, keep):
        self.host_port = host_port
        self.keep = keep
        self.api = Api(f"http://localhost:{host_port}")
        self.n = 0

    def fresh_env(self, workers):
        """Fresh storage: a new empty DATA_DIR per scenario."""
        self.n += 1
        data_dir = os.path.join(TMP_ROOT, f"data-{self.n}-{uuid.uuid4().hex[:8]}")
        os.makedirs(data_dir, exist_ok=True)
        compose = Compose(data_dir, self.host_port)
        compose.down()  # release the previous scenario's containers/ports
        compose.up(worker_count=workers)
        wait_health(self.api)
        return Env(self.api, compose)


class Env:
    def __init__(self, api, compose):
        self.api = api
        self.compose = compose

    def restart(self, worker_count=4):
        """Restart with the same DATA_DIR (same persisted data)."""
        self.compose.up(worker_count=worker_count)
        wait_health(self.api)


# ---------------------------------------------------------------- assertions


def require(cond, msg):
    if not cond:
        raise AssertionError(msg)


def states_of(history):
    return [h["state"] for h in history]


def overlap_max(intervals):
    """Max simultaneous open intervals [(start, end)]. End==start counts as

    no overlap (finished frees the worker first)."""
    points = []
    for s, e in intervals:
        points.append((s, +1))
        points.append((e, -1))
    points.sort(key=lambda p: (p[0], p[1]))  # ends before starts at same instant
    cur = best = 0
    for _, d in points:
        cur += d
        best = max(best, cur)
    return best


def ev_started(api, job_id):
    return get_events(api, job_id, "started")


def ev_finished(api, job_id):
    return get_events(api, job_id, "finished")


def job_field(job, name):
    require(name in job, f"job detail missing field {name!r}: {sorted(job)}")
    return job[name]


# ------------------------------------------------------------------ scenarios


@scenario("S1 immediate job runs and records history")
def s1(env):
    api = env.api
    status, body = api.create("succeed-after", {"delayMs": 0})
    require(status == 202 and isinstance(body.get("jobId"), str),
            f"create -> {status} {body}")
    job = wait_state(api, body["jobId"], "succeeded", timeout=30)

    require(states_of(job_field(job, "history")) ==
            ["enqueued", "processing", "succeeded"],
            f"history states: {states_of(job['history'])}")
    attempts = job_field(job, "attemptHistory")
    require(len(attempts) == 1, f"attemptHistory len {len(attempts)}")
    a = attempts[0]
    require(a["attempt"] == 1 and a["outcome"] == "succeeded"
            and a["error"] is None and a["finishedAt"] is not None,
            f"attempt row: {a}")
    require(job_field(job, "attempts") == 1, f"attempts {job['attempts']}")
    require(job_field(job, "scheduledAt") is None, "scheduledAt not null")

    fin = ev_finished(api, job["id"])
    require(len(fin) == 1 and fin[0]["detail"].get("outcome") == "ok",
            f"finished events: {fin}")
    return [f"job {job['id']} succeeded; history={states_of(job['history'])}",
            f"attempts={attempts}"]


@scenario("S2 scheduled and past-due jobs respect runAt and start SLA")
def s2(env):
    api = env.api
    run_at = utcnow() + timedelta(seconds=2.5)
    status, fut = api.create("succeed-after", {"delayMs": 0},
                             {"runAt": iso(run_at)})
    require(status == 202, f"future create -> {status} {fut}")

    # while waiting, nothing may start before runAt
    deadline = run_at - timedelta(seconds=0.3)
    while utcnow() < deadline:
        require(not ev_started(api, fut["jobId"]),
                "scheduled job started before runAt")
        time.sleep(0.1)

    started = wait_until(lambda: ev_started(api, fut["jobId"]),
                         START_SLA_S + 3, desc="future job started")
    fut_st = parse_at(started[0]["at"])
    require(fut_st >= run_at - timedelta(seconds=EPS),
            f"started {iso(fut_st)} before runAt {iso(run_at)}")
    require(fut_st <= run_at + timedelta(seconds=START_SLA_S + EPS),
            f"started {iso(fut_st)} > runAt+{START_SLA_S}s")
    wait_state(api, fut["jobId"], "succeeded", timeout=30)

    status, past = api.create("succeed-after", {"delayMs": 0},
                              {"runAt": iso(utcnow() - timedelta(minutes=5))})
    require(status == 202, f"past create -> {status} {past}")
    t0 = utcnow()
    started = wait_until(lambda: ev_started(api, past["jobId"]),
                         START_SLA_S + 3, desc="past-due job started")
    past_st = parse_at(started[0]["at"])
    require(past_st <= t0 + timedelta(seconds=START_SLA_S + EPS),
            f"past-due job started at {iso(past_st)}")
    return [f"future job started at {iso(fut_st)} (runAt {iso(run_at)})",
            f"past-due job started at {iso(past_st)} (created {iso(t0)})"]


@scenario("S3 FIFO order with a single worker")
def s3(env):
    api = env.api
    ids = []
    for msg in ["A", "B", "C"]:
        status, body = api.create("log-message", {"message": msg})
        require(status == 202, f"create {msg} -> {status} {body}")
        ids.append(body["jobId"])
    for jid in ids:
        wait_state(api, jid, "succeeded", timeout=30)
    msgs = get_events(api, etype="message")
    require([m["detail"]["message"] for m in msgs] == ["A", "B", "C"],
            f"message order: {[m['detail'] for m in msgs]}")
    return [f"messages observed in order: {[m['detail']['message'] for m in msgs]}"]


@scenario("S4 concurrency capped at WORKER_COUNT, no duplicate execution")
def s4(env):
    api = env.api
    ids = []
    for _ in range(6):
        status, body = api.create("succeed-after", {"delayMs": 1500})
        require(status == 202, f"create -> {status} {body}")
        ids.append(body["jobId"])
    for jid in ids:
        wait_state(api, jid, "succeeded", timeout=60)

    intervals = []
    per_job_starts = {}
    for e in get_events(api, etype="started"):
        per_job_starts[e["jobId"]] = per_job_starts.get(e["jobId"], 0) + 1
    for e in get_events(api, etype="finished"):
        st = [s for s in get_events(api, e["jobId"], "started")]
        require(len(st) == 1, f"job {e['jobId']} started {len(st)} times")
        intervals.append((parse_at(st[0]["at"]), parse_at(e["at"])))
    require(all(v == 1 for v in per_job_starts.values()),
            f"duplicate starts: {per_job_starts}")
    peak = overlap_max(intervals)
    require(1 < peak <= 2, f"peak concurrency {peak} (want 2)")
    return [f"peak concurrency observed: {peak}; starts per job: "
            f"{sorted(set(per_job_starts.values()))}"]


@scenario("S5 fixed retry delay and eventual success")
def s5(env):
    api = env.api
    status, body = api.create("fail-n-times", {"failTimes": 2},
                              {"maxAttempts": 3, "retryDelayMs": 500})
    require(status == 202, f"create -> {status} {body}")
    job = wait_state(api, body["jobId"], "succeeded", timeout=60)

    attempts = job_field(job, "attemptHistory")
    require([a["attempt"] for a in attempts] == [1, 2, 3],
            f"attempts: {[a['attempt'] for a in attempts]}")
    require([a["outcome"] for a in attempts] ==
            ["failed", "failed", "succeeded"],
            f"outcomes: {[a['outcome'] for a in attempts]}")
    gaps = []
    for prev, nxt in zip(attempts, attempts[1:]):
        gap = (parse_at(nxt["startedAt"]) - parse_at(prev["finishedAt"])).total_seconds()
        gaps.append(round(gap, 3))
        require(gap >= 0.5 - EPS, f"retry gap {gap}s < 500ms")
    require(all(a["error"] == "demo failure" for a in attempts[:2]),
            f"errors: {[a['error'] for a in attempts]}")
    return [f"outcomes={[a['outcome'] for a in attempts]}; gaps={gaps}s"]


@scenario("S6 attempt exhaustion -> failed, other jobs unaffected")
def s6(env):
    api = env.api
    status, bad = api.create("fail-n-times", {"failTimes": 10},
                             {"maxAttempts": 3, "retryDelayMs": 100})
    status2, good = api.create("succeed-after", {"delayMs": 0})
    require(status == 202 and status2 == 202, f"create {status}/{status2}")

    badjob = wait_state(api, bad["jobId"], "failed", timeout=60)
    goodjob = wait_state(api, good["jobId"], "succeeded", timeout=30)
    attempts = job_field(badjob, "attemptHistory")
    require(len(attempts) == 3 and all(a["outcome"] == "failed" for a in attempts),
            f"attempts: {attempts}")
    require(badjob["attempts"] == 3, f"attempts={badjob['attempts']}")
    return [f"failing job -> failed after {len(attempts)} attempts",
            f"companion job {goodjob['id']} -> succeeded"]


@scenario("S7 delete pending, requeue finished, 409 on wrong states")
def s7(env):
    api = env.api
    # delete a scheduled job: it must never execute
    run_at = utcnow() + timedelta(seconds=1.5)
    status, sched = api.create("succeed-after", {"delayMs": 0},
                               {"runAt": iso(run_at)})
    require(status == 202, f"create -> {status}")
    status, body = api.delete(sched["jobId"])
    require(status == 200 and body.get("state") == "deleted",
            f"delete -> {status} {body}")
    time.sleep((run_at - utcnow()).total_seconds() + 2)
    require(not ev_started(api, sched["jobId"]),
            "deleted job executed after runAt")

    # requeue a finished job: counter resets, old history preserved
    status, done = api.create("succeed-after", {"delayMs": 0})
    require(status == 202, f"create -> {status}")
    wait_state(api, done["jobId"], "succeeded", timeout=30)
    status, rq = api.requeue(done["jobId"])
    require(status == 202, f"requeue -> {status} {rq}")
    job = wait_state(api, done["jobId"], "succeeded", timeout=30)
    attempts = job_field(job, "attemptHistory")
    require([a["attempt"] for a in attempts] == [1, 1],
            f"attempt numbering after requeue: {[a['attempt'] for a in attempts]}")
    hist = states_of(job_field(job, "history"))
    require(hist.count("succeeded") == 2 and hist.count("enqueued") == 2,
            f"history after requeue: {hist}")

    # 409 conflicts
    status, run = api.create("succeed-after", {"delayMs": 4000})
    wait_until(lambda: ev_started(api, run["jobId"]), 15, desc="job running")
    status, body = api.delete(run["jobId"])
    require(status == 409 and "error" in body, f"delete processing -> {status} {body}")
    status, body = api.requeue(run["jobId"])
    require(status == 409, f"requeue processing -> {status} {body}")
    wait_state(api, run["jobId"], "succeeded", timeout=30)
    status, body = api.delete(run["jobId"])
    require(status == 409, f"delete succeeded -> {status} {body}")
    return ["deleted scheduled job never executed",
            f"requeue preserved history: {hist}; attempts={[a['attempt'] for a in attempts]}",
            "409 returned for delete/requeue on incompatible states"]


@scenario("S8 kill -9 preserves jobs, args, history and schedule")
def s8(env):
    api = env.api
    status, done = api.create("log-message", {"message": "persist-me"})
    require(status == 202, f"create -> {status}")
    finished_before = wait_state(api, done["jobId"], "succeeded", timeout=30)

    run_at = utcnow() + timedelta(seconds=3)
    status, sched = api.create("succeed-after", {"delayMs": 0},
                               {"runAt": iso(run_at)})
    status2, pend = api.create("succeed-after", {"delayMs": 200})
    require(status == 202 and status2 == 202, "creates failed")

    env.compose.kill_demo()
    env.restart()

    after = wait_state(api, done["jobId"], "succeeded", timeout=30)
    require(job_field(after, "args") == {"message": "persist-me"},
            f"args after restart: {after['args']}")
    require(job_field(after, "history") == finished_before["history"],
            "history changed across restart")
    require(job_field(after, "attemptHistory") == finished_before["attemptHistory"],
            "attemptHistory changed across restart")

    wait_state(api, sched["jobId"], "succeeded", timeout=30)
    wait_state(api, pend["jobId"], "succeeded", timeout=30)
    return ["finished job kept args/history/attempts across kill -9",
            "scheduled and pending jobs processed after restart"]


@scenario("S9 interrupted attempt counts toward maxAttempts")
def s9(env):
    api = env.api
    # budget remains: retried after kill
    status, body = api.create("succeed-after", {"delayMs": 4000},
                              {"maxAttempts": 2, "retryDelayMs": 0})
    jid = body["jobId"]
    wait_until(lambda: ev_started(api, jid), 15, desc="job running before kill")
    env.compose.kill_demo()
    env.restart()
    job = wait_state(api, jid, "succeeded", timeout=60)
    attempts = job_field(job, "attemptHistory")
    require([a["outcome"] for a in attempts] == ["interrupted", "succeeded"],
            f"outcomes: {[a['outcome'] for a in attempts]}")
    require([a["attempt"] for a in attempts] == [1, 2],
            f"attempt numbers: {[a['attempt'] for a in attempts]}")

    # no budget: interrupted exhausts maxAttempts -> failed
    status, body2 = api.create("succeed-after", {"delayMs": 4000},
                               {"maxAttempts": 1})
    jid2 = body2["jobId"]
    wait_until(lambda: ev_started(api, jid2), 15, desc="job running before kill")
    env.compose.kill_demo()
    env.restart()
    job2 = wait_state(api, jid2, "failed", timeout=30)
    attempts2 = job_field(job2, "attemptHistory")
    require(len(attempts2) == 1 and attempts2[0]["outcome"] == "interrupted",
            f"attempts: {attempts2}")
    require(job2["attempts"] == 1, f"attempts={job2['attempts']}")
    return ["maxAttempts=2: interrupted attempt 1 -> retried -> succeeded",
            "maxAttempts=1: interrupted attempt 1 -> job failed"]


@scenario("S10 graceful stop drains running job, pending one waits")
def s10(env):
    api = env.api
    status, running = api.create("succeed-after", {"delayMs": 4000})
    status2, pending = api.create("log-message", {"message": "after-stop"})
    require(status == 202 and status2 == 202, "creates failed")
    wait_until(lambda: ev_started(api, running["jobId"]), 15,
               desc="first job running")

    # snapshot events while the demo drains
    last_events = []
    def poll():
        nonlocal last_events
        try:
            s, b = api.events()
            if s == 200:
                last_events = b["items"]
        except ApiError:
            pass
    stopper = env.compose.stop_demo_async(timeout_s=60)
    while stopper.is_alive():
        poll()
        time.sleep(0.1)
    poll()
    stopper.join(timeout=70)
    require(not stopper.is_alive(), "demo did not stop within timeout")
    t_stop = utcnow()

    rjid, pjid = running["jobId"], pending["jobId"]
    fin = [e for e in last_events if e.get("jobId") == rjid and e["type"] == "finished"]
    require(fin and fin[0]["detail"].get("outcome") == "ok",
            f"running job did not finish during drain: {fin}")
    require(not any(e.get("jobId") == pjid for e in last_events),
            "pending job started during shutdown")

    env.restart(worker_count=1)
    pj = wait_state(api, pjid, "succeeded", timeout=30)
    rj = api.job(rjid)[1]

    # post-restart proof from persisted timestamps (DB clock):
    # running job finished before the stop; pending job's first attempt
    # started only after the process went down and came back.
    r_fin = parse_at(rj["attemptHistory"][0]["finishedAt"])
    p_start = parse_at(pj["attemptHistory"][0]["startedAt"])
    slack = timedelta(seconds=1.0)  # host/DB clock skew allowance
    require(r_fin <= t_stop + slack,
            f"running job finishedAt {iso(r_fin)} after stop {iso(t_stop)}")
    require(p_start >= t_stop - slack,
            f"pending job startedAt {iso(p_start)} before stop {iso(t_stop)}")
    return [f"running job finished at {iso(r_fin)} (stop {iso(t_stop)})",
            f"pending job first attempt at {iso(p_start)} — after restart"]


# ------------------------------------------------------------- error contract


def contract(api):
    """Error/format contract checks that need a running environment."""

    checks = []
    def check(name, cond, got):
        checks.append((name, cond, got))
        require(cond, f"{name}: got {got}")

    st, b = api.request("POST", "/demo/jobs", raw=b"{ not json")
    check("malformed JSON -> 400", st == 400 and isinstance(b, dict) and "error" in b, (st, b))
    st, b = api.request("POST", "/demo/jobs", {"job": "succeed-after"})
    check("missing params -> 400", st == 400 and "error" in b, (st, b))
    st, b = api.create("no-such-job", {"x": 1})
    check("unknown job -> 400", st == 400 and "error" in b, (st, b))
    for params in [{"delayMs": -1}, {"delayMs": 60001}, {"delayMs": "x"}, {}]:
        st, b = api.create("succeed-after", params)
        check(f"bad delayMs {params} -> 400", st == 400 and "error" in b, (st, b))
    for params in [{"failTimes": -1}, {"failTimes": 11}, {}]:
        st, b = api.create("fail-n-times", params)
        check(f"bad failTimes {params} -> 400", st == 400 and "error" in b, (st, b))
    st, b = api.create("log-message", {"message": "x" * 201})
    check("message >200 -> 400", st == 400 and "error" in b, (st, b))
    for opts in [{"maxAttempts": 0}, {"maxAttempts": 11},
                 {"retryDelayMs": -1}, {"retryDelayMs": 60001},
                 {"runAt": "not-a-date"}]:
        st, b = api.create("succeed-after", {"delayMs": 0}, opts)
        check(f"bad options {opts} -> 400", st == 400 and "error" in b, (st, b))

    missing = str(uuid.uuid4())
    for method, path in [("GET", f"/api/jobs/{missing}"),
                         ("DELETE", f"/api/jobs/{missing}"),
                         ("POST", f"/api/jobs/{missing}/requeue")]:
        st, b = api.request(method, path)
        check(f"{method} {path.split('/')[2]} -> 404", st == 404 and "error" in b, (st, b))

    st, b = api.create("succeed-after", {"delayMs": 0})
    check("create -> 202 jobId", st == 202 and isinstance(b.get("jobId"), str), (st, b))
    job = wait_state(api, b["jobId"], "succeeded", timeout=30)
    for field in ["id", "name", "args", "state", "attempts", "maxAttempts",
                  "retryDelayMs", "scheduledAt", "createdAt", "updatedAt",
                  "history", "attemptHistory"]:
        require(field in job, f"job detail missing {field}")
    check("scheduledAt null when not scheduled", job["scheduledAt"] is None,
          job["scheduledAt"])
    h = job["history"][0]
    check("history entry shape",
          set(h) >= {"state", "at", "reason"}, h)
    a = job["attemptHistory"][0]
    check("attempt entry shape",
          set(a) >= {"attempt", "startedAt", "finishedAt", "outcome", "error"}, a)

    st, b = api.jobs()
    check("GET /api/jobs -> items with summary",
          st == 200 and isinstance(b, dict) and isinstance(b.get("items"), list)
          and all(set(i) >= {"id", "name", "state", "attempts"} for i in b["items"]),
          (st, b if st != 200 else f"{len(b['items'])} items"))

    # scheduled job shows scheduledAt while pending
    st, b = api.create("succeed-after", {"delayMs": 0},
                       {"runAt": iso(utcnow() + timedelta(minutes=5))})
    st, job2 = api.job(b["jobId"])
    check("scheduledAt present while scheduled",
          job2.get("scheduledAt") is not None, job2.get("scheduledAt"))
    api.delete(b["jobId"])

    st, b = api.health()
    check("GET /health -> 200 ok", st == 200 and b == {"status": "ok"}, (st, b))
    return [f"{len(checks)} contract checks passed"]


# ---------------------------------------------------------------------- main


def preflight():
    if not os.path.exists(COMPOSE_FILE):
        print("BLOCKED: docker-compose.yml not found — implementation pending.")
        return 2
    proc = subprocess.run(["docker", "info"], capture_output=True, timeout=30)
    if proc.returncode != 0:
        print("BLOCKED: docker daemon is not running.")
        return 2
    return 0


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--keep", action="store_true",
                   help="keep containers running after each scenario")
    p.add_argument("--port", type=int, default=18080, help="host port for the demo")
    p.add_argument("--only", type=str, default="",
                   help="comma-separated scenario numbers to run, e.g. 1,5")
    args = p.parse_args()

    code = preflight()
    if code:
        return code

    ctx = Ctx(args.port, args.keep)
    os.makedirs(TMP_ROOT, exist_ok=True)

    print("building demo image...")
    Compose(os.path.join(TMP_ROOT, "build-probe"), args.port).build()

    workers = {s3: 1, s4: 2, s10: 1}
    selected = {int(x) for x in args.only.split(",") if x.strip()}
    order = [(1, s1), (2, s2), (3, s3), (4, s4), (5, s5),
             (6, s6), (7, s7), (8, s8), (9, s9), (10, s10)]
    for num, fn in order:
        if selected and num not in selected:
            continue
        fn(ctx, workers=workers.get(fn, 4))

    # error contract needs a live env
    if not selected or 0 in selected:
        env = ctx.fresh_env(4)
        try:
            record("C0 API error/format contract", True, contract(env.api))
        except Exception as e:  # noqa: BLE001
            record("C0 API error/format contract", False, [f"{type(e).__name__}: {e}"])
        finally:
            if not args.keep:
                env.compose.down()

    failed = [n for n, ok, _ in RESULTS if not ok]
    print(f"\n{len(RESULTS) - len(failed)}/{len(RESULTS)} checks passed")
    if failed:
        print("failed:", ", ".join(failed))
    if not args.keep:
        shutil.rmtree(TMP_ROOT, ignore_errors=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
