# Desenvolvimento

O projeto usa .NET SDK 10 e Avalonia; a aplicação e suas integrações nativas
visam Windows. O código de replay incluído em `lib` possui patches e licenças
próprias: consulte [`lib/PATCHES.md`](../../lib/PATCHES.md).

Os projetos estão em `src/ChestCounter`, `src/ChestCounter.Core`,
`src/ChestCounter.Application` e `src/ChestCounter.Infrastructure`. Core,
Application e Infrastructure compilam os fontes locais; o projeto desktop
mantém inclusão C# explícita e os recursos Avalonia. Consulte
[a arquitetura atual](../architecture/overview.md) antes de mover arquivos.

O fluxo existente de publicação Windows está em [`build.bat`](../../build.bat);
o script verifica os textos de localização e publica um executável
self-contained como `ContadorDeBau.exe` em `dist`. O fluxo de CI está em
[`.github/workflows/build.yml`](../../.github/workflows/build.yml). Consulte os
scripts para suas opções exatas. Não há migração de dados associada à nova
escala: configurações anteriores recebem o valor padrão pelo inicializador.
