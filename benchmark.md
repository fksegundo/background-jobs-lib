Benchmark de workflows de desenvolvimento com LLM
1. Objetivo

Entender como cada pessoa do time trabalha com LLM e quais resultados cada abordagem produz, a partir de um mesmo projeto executado por todos, cada um do seu jeito.

Não é um benchmark de "qual modelo resolve melhor a task X" nem a validação de workflows pré-definidos. É um estudo exploratório do processo humano + agente: os padrões e estratégias emergem da comparação, e os aprendizados alimentam rodadas seguintes.

Perguntas de interesse

Servem de guia para o que observar e discutir, não para fechar o desenho:

* Como cada pessoa especifica o problema e quanto detalha antes de executar (spec, plano, nada)?
* Como quebra o trabalho (granularidade) e como cria spec/plano em conjunto com o modelo?
* Usa SDD ou não? Em que momentos?
* Onde o humano intervém (checkpoints vs autonomia)?
* Como confere o resultado pós-execução?
* Quanto tempo humano, tokens e retrabalho cada abordagem consumiu?
* Quanto do resultado parece vir da estratégia vs do modelo/harness/pessoa?

2. Estudos e referências relacionadas

2.1 Benchmarks de modelo/harness (referência de infraestrutura e estatística)

Úteis para desenhar a coleta de métricas, não para o foco do nosso estudo (a maioria é "lista de tasks fixas × troca de modelo/harness").

Material
	Resumo

Databricks — Benchmarking Coding Agents on a Multi-Million Line Codebase
	Compara modelos e harnesses no monorepo da empresa, com tarefas reconstruídas de PRs reais e testes ocultos; mede resolução, tokens e custo por tarefa.

OpenBench · release
	Compara harnesses com modelo e tarefas fixos, verificadores executáveis e execuções repetidas; reporta correção, eficiência e intervalos de confiança.

PawBench
	Avalia combinações de modelos e harnesses em 150 tarefas, com verificadores automáticos, julgamento por LLM e análise das trajetórias de execução.

Same Model, Ten Harnesses (Solvency)
	Analisa desempenho e custo de harnesses com modelos fixos, combinando benchmarks públicos e experimento próprio; detalha consumo de tokens e uso de cache.

coding-agent-bench
	Compara geração direta via API e agentes com ferramentas em nove tarefas Python, usando testes ocultos e heurísticas de qualidade.

terminal-agent-bench
	Harness Docker para avaliar agentes de terminal em 30 tarefas de correção, funcionalidade, refatoração, migração e investigação, com critérios executáveis, tempo e custo.

SWE Atlas (Scale AI)
	Avalia compreensão de codebases, escrita de testes e refatoração em 284 tarefas; combina rubricas, testes de mutação e regressão, medindo resolução e consistência.

OmniCode · SWE-Compass
	Benchmarks de engenharia de software com múltiplas categorias e linguagens: OmniCode amplia PRs com patches, reviews e linters; SWE-Compass reúne 2.000 tarefas com avaliações específicas.

SkillsBench
	Compara execuções das mesmas tarefas profissionais com e sem skills, em ambientes isolados e com verificadores de resultados; mede resolução e eficiência.

State of AI Harness Engineering 2026 (Marmelab)
	Panorama baseado na auditoria de repositórios e síntese de publicações; mapeia práticas de instrução, controles executáveis, testes, avaliações, observabilidade e manutenção de harnesses.

harness-bench
	Protótipo de benchmark com tarefas, modelos e orçamentos controlados; varia contexto, skills e retestes, registrando eficiência, falhas operacionais e declarações indevidas de sucesso.

Fullscript — Benchmarking the Agents That Write Our Code
	Compara modelos, skills e harnesses em 36 tarefas extraídas do histórico Git da empresa, com testes ocultos, verificações de contratos e rubricas.


2.2 Estudos sobre o processo humano + agente (mais próximos do nosso objetivo)

Material
	Resumo

CentaurEval — Human-in-the-Loop Value in Agentic Coding
	Compara humanos, agentes e diferentes níveis de colaboração em tarefas Python desenhadas para exigir raciocínio humano e implementação assistida, com 45 participantes.

SWE-RPG · repo
	Avalia agentes autônomos em 163 tarefas reais, com testes e referências de requisitos e planos; analisa trajetórias para localizar falhas por etapa.

ICAE-Bench
	Avalia construção de projetos a partir de requisitos vagos, com esclarecimentos por usuário simulado; combina testes funcionais e diagnósticos de estrutura, qualidade e interação.

Mise en Place for Agentic Coding
	Propõe preparar contexto, especificação colaborativa e tarefas com dependências antes da implementação; ilustra a metodologia em um hackathon, com agentes paralelos.

Grounding AI Agents in Contracts (Google)
	Compara geração direta de testes com extração prévia de contratos comportamentais em 90 bugs reais; mede detecção, cobertura, qualidade e consumo de tokens.

The Anatomy of Harness Engineering (Google)
	Apresenta avaliações de comportamentos observáveis, como esclarecer requisitos e validar mudanças, para complementar benchmarks end-to-end e detectar regressões no harness.

Uvik — Spec-Driven Development Benchmark
	Relata comparação de quatro frameworks SDD e controle sem spec em 50 tickets Python, com revisão cega; apresenta inconsistência na data de execução.

SPECMINE
	Corpus de specs e artefatos SDD de repositórios públicos, enriquecidos com estrutura, histórico, PRs e referências ao código para estudar adoção e evolução.

How to Write a Good Spec for AI Agents (O'Reilly)
	Guia para elaborar specs com agentes: visão inicial, documentação estruturada, contexto modular, limites e validação contínua, com revisão humana entre etapas.


2.3 Relatos práticos (blogs)

Material
	Resumo

Leading EDJE — Teaching an AI to Build Software
	Reconstrói um agendador de entrevistas cinco vezes com Claude Code e Spec Kit, ajustando templates, regras executáveis e testes E2E a cada iteração.

OrangeLoops — Spec Kit + Claude Code case study
	Relata construção de aplicativo NestJS/Expo com Spec Kit e Claude Code: cerca de duas horas de geração registradas parcialmente e quatro dias de estabilização.

Light SDD com Claude Code e Codex (Zenn)
	Executa spec → plano → implementação → verificação com Claude Code e Codex, usando IA também como leitor e revisor; observa diferenças nas decisões levantadas.

Research, Plan, Implement (Notes by Lex)
	Relata workflow com pesquisa da codebase, revisão do entendimento e plano iterativo antes de implementar; adapta documentação e TDD à tarefa e revisa o código.

Claude Code in a Crunch (subaud)
	Compara prompts urgentes e sem pressa em três tarefas; revisão cega por agente encontra bugs nas versões urgentes e mitigação parcial com regras explícitas.

Daniel Ancuta — CLI com Spec Kit + Claude
	Relata criação de CLI em Go com Spec Kit e Claude Code, sem escrever código manualmente; descreve cerca de quinze horas e retrabalho arquitetural.

Jean Desauw — Agentic coding without babysitting
	Relata organização de componentes, specs com checkpoints e feedback via issues; concentra a intervenção humana na arquitetura, primeira versão e revisão em produção.


2.4 Ordem de leitura sugerida

1. Leading EDJE (5 iterações do mesmo projeto)
2. Databricks (como transformar PR real em task)
3. SWE-RPG (separar requisito / plano / código na avaliação)
4. Mise en Place (preparação de contexto)
5. CentaurEval (humano como parte do sistema)
6. OrangeLoops e Light SDD (relatos práticos)
7. Google — Anatomy of Harness Engineering (avaliação comportamental)

2.5 Lições que informam o desenho

* Benchmarkar workflow, não só modelo: dev + modelo + harness + skills + MCP + estratégia + effort.
* Poucas execuções ≠ conclusão: variância é alta; repetir ou ao menos reportar a incerteza.
* Medir tempo humano separado de tempo do agente.
* Código gerado e "passou nos testes" não bastam; olhar retrabalho, escopo e processo.
* Não existe workflow universalmente melhor; o ideal provavelmente depende da complexidade da task.

3. Proposta: estudo exploratório com o time

3.1 Princípio

Não definimos workflows nem hipóteses a validar. Cada pessoa resolve o mesmo projeto, com o mesmo enunciado e contexto, do jeito que achar melhor, registra o que fez, e depois o time compara e conversa para entender como cada um trabalhou e o que resultou disso. Os padrões e estratégias emergem da análise, não são impostos antes.

Projeto comum
   → cada pessoa executa livremente + registra
   → time compartilha e discute
   → correlaciona resultados com estratégias
   → perfila as abordagens
   → aprendizados
   → rodada 2 (com o que aprendemos)

É um estudo observacional/qualitativo, não um experimento controlado: o objetivo é gerar perguntas e hipóteses melhores para a rodada seguinte.

3.2 O que cada pessoa faz

* Escolhe livremente modelo, harness, effort, skills, MCPs, uso de spec/plano/SDD, granularidade, quando intervir.
* Trabalha em um repositório/branch próprio a partir do mesmo ponto de partida.
* Registra durante o trabalho (ver 3.3), sem mudar o jeito de trabalhar por causa do registro além do necessário.
* Anota decisões e motivos ("por que fiz spec antes", "por que troquei de modelo").

3.3 O que registrar

Registro leve, para não atrapalhar o fluxo. Quanto mais automático, melhor.

Item
	Como coletar

Prompts e transcript da sessão
	Exportar o histórico do harness (Claude Code, Codex, etc.) ao final

Artefatos gerados
	Guardar specs, planos, listas de tarefas, arquivos de regras/skills usados

Setup
	Modelo(s), harness, effort, skills, MCPs, configurações (CLAUDE.md/AGENTS.md etc.)

Horas humanas
	Registro simples por fase: início/fim (entender, especificar, planejar, executar, revisar/corrigir)

Tempo do agente
	Estimado pelo transcript/logs quando disponível

Tokens e custo
	Relatório de uso do harness/provedor (in/out/cache)

Nº de turnos e intervenções
	Contagem a partir do transcript

Resultado
	Código final, estado dos testes, o que ficou pendente

Diário curto
	Onde travou, retrabalho, surpresas, o que faria diferente

Autoavaliação
	Satisfação com o resultado e com o processo


Um template único (planilha ou markdown) para todos preencherem facilita a comparação depois.

3.4 Avaliação do resultado

Medir o que dá para comparar entre soluções diferentes do mesmo projeto:

* Funcional: atende ao critério de aceite? testes/cenários comuns passando, bugs encontrados.
* Qualidade de engenharia: arquitetura, legibilidade, manutenibilidade, aderência ao escopo, tamanho do diff, cobertura de testes.
* Eficiência: horas humanas, tempo total, tokens/custo.
* Experiência: autoavaliação do participante.

3.5 Sessão de compartilhamento

Depois da execução, o time se reúne para:

1. Cada pessoa apresentar: como começou, que artefatos criou, como interagiu com o agente, onde interveio, o que deu errado, resultado e números.
2. Ver exemplos reais de prompts, specs e planos uns dos outros.
3. Perguntar "por quê" sobre as escolhas.
4. Observar contrastes: mesmo resultado com esforços muito diferentes, mesma abordagem com resultados diferentes, etc.

3.6 Análise: correlacionar, inferir, perfilar

A partir dos registros e da conversa:

1. Classificar a estratégia de cada pessoa nas dimensões que aparecerem (por exemplo: quanto especificou antes de codar, se fez plano, granularidade, quem escreveu a spec, frequência de intervenção, uso de subagents/skills, validação). As categorias saem dos dados, não de uma lista prévia.
2. Correlacionar com resultados: qualidade, horas humanas, tokens/custo, retrabalho, satisfação.
3. Perfilar: agrupar pessoas/abordagens em perfis (ex.: "especifica muito e deixa rodar" vs "iterativo com checkpoints curtos"), com pontos fortes e fracos observados.
4. Separar o que parece vir da estratégia do que parece vir da pessoa, do modelo ou do harness, sabendo que com poucos participantes isso é só indicativo.
5. Registrar aprendizados e perguntas novas.

3.7 Rodadas seguintes

Com os aprendizados da rodada 1, as próximas podem:

* Repetir com outro projeto, adotando ou testando práticas que pareceram boas (aí sim com algo mais direcionado).
* Pedir que alguém tente a abordagem de outro colega.
* Variar o tipo/complexidade do projeto.
* Refinar o que registramos e a rubrica.

