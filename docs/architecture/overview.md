# Arquitetura atual

Os fontes de Core, Application e Infrastructure ficam em seus respectivos
projetos. O desktop mantém a UI e o ponto de composição.

## Projetos e dependências reais

```text
Desktop (ChestCounter) -> Application, Infrastructure, Core
Application -> Core
Infrastructure -> Application, Core, FortniteReplayReader -> Unreal.Core, Unreal.Encryption, OozSharp
Core -> biblioteca padrão .NET
```

`Core`, `Application` e `Infrastructure` compilam os fontes locais por inclusão
padrão do SDK. `Application` define contratos para o monitor, e
`Infrastructure.MonitorServices` adapta os serviços existentes a esses
contratos. O desktop instancia esse adaptador e injeta os contratos ao criar
`Monitor`; também mantém `Store` e `Tracker` concretos no `RuntimeContext` para
as telas e a inicialização. Não há contêiner de DI.

`Core` ainda reúne configuração, localização e formatação de apresentação,
além dos modelos e regras compartilhados; uma separação mais fina desses tipos
continua fora do escopo desta reorganização. `Monitor` também usa diretamente
algumas APIs padrão de sistema de arquivos e processos do .NET; extraí-las para
portas próprias seria uma refatoração futura, não necessária para remover a
dependência de Application em Infrastructure.

## Inventário de responsabilidades

| Arquivo | Responsabilidade e dependências principais | Camada atual / destino provável |
|---|---|---|
| `Models.cs` | Registros de partida, configuração e dados persistidos; JSON e regras de contagem | Core / separar domínio de configuração |
| `ChestKinds.cs` | Classificação de contêineres | Core / domínio |
| `Profiles.cs` | Filtragem, seleção e agregação por perfil | Core / domínio ou aplicação |
| `TreasureNames.cs` | Nomes de tesouros e localização | Core / aplicação |
| `Lang.cs`, `Translations.cs` | Idiomas, cultura e textos | Core / apresentação ou localização |
| `OverlayState.cs` | Estado e dimensões compartilhadas do overlay | Core / contrato de apresentação |
| `OverlayModel.cs` | Projeta partidas e perfil em estado formatado | Core / aplicação |
| `IOverlayHost.cs` | Contrato de exibição e atualização | Application / aplicação |
| `Monitor.cs` | Ciclo de vida, estado, comandos e publicação; depende dos contratos de Application | Application / aplicação |
| `IMonitorServices.cs` | Portas pequenas para armazenamento, rastreamento, relatório, OBS, atualização, jogo, log e caminho do executável | Application / contratos |
| `MonitorServices.cs` | Adapta as implementações existentes de Infrastructure aos contratos do monitor | Infrastructure / adaptadores |
| `Store.cs` | Caminhos, leitura e gravação JSON | Infrastructure / infraestrutura |
| `Tracker.cs` | Monitora e processa arquivos, histórico e progresso; usa `Store`, `ReplayAnalyzer` e relatórios | Infrastructure / separar orquestração e adaptadores |
| `ReplayAnalyzer.cs` | Interpreta eventos usando bibliotecas de replay | Infrastructure / adaptador de replay |
| `ReportWriter.cs` | Gera relatório, CSV e páginas de overlay | Infrastructure / infraestrutura de saída |
| `ObsServer.cs` | Servidor HTTP local, imagem e escala do OBS | Infrastructure / infraestrutura |
| `FortniteWatch.cs` | Processo do jogo e inicialização no Windows | Infrastructure / infraestrutura |
| `Updater.cs` | Verificação e instalação de atualizações | Infrastructure / infraestrutura |
| `AppInfo.cs` | Versão, URLs, executável e mutexes | Infrastructure / separar metadados e serviços de plataforma |
| `ErrorLog.cs` | Registro de erros em arquivo | Infrastructure / infraestrutura |
| `EcoQos.cs` | Ajusta política de energia do processo Windows | Infrastructure / infraestrutura |
| `NativeConsole.cs` | Console nativo Windows | Infrastructure / infraestrutura |
| `ConsoleUi.cs` | Mensagens e progresso no console | Infrastructure / apresentação de console |
| `Png.cs` | Geração auxiliar de PNG | Infrastructure / infraestrutura gráfica |
| `Program.cs` | Argumentos, composição manual e inicialização | Desktop / composição |
| `App.axaml`, `App.axaml.cs` | Recursos globais Avalonia e contexto de execução | Desktop / apresentação e composição |
| `ViewModels/DashboardViewModel.cs` | Estado e comandos do dashboard | Desktop / apresentação |
| `ViewModels/SettingsViewModel.cs` | Estado e comandos das configurações | Desktop / apresentação |
| `Views/MainWindow.axaml`, `.axaml.cs` | Janela principal e eventos de interface | Desktop / apresentação |
| `Views/SettingsWindow.axaml`, `.axaml.cs` | Configurações, prévia e eventos de interface | Desktop / apresentação |
| `Views/InfoDialogWindow.axaml`, `.axaml.cs`, `Views/StartupNoticeWindow.cs` | Diálogos e avisos | Desktop / apresentação |
| `Views/DashboardComponents.cs` e arquivos `.axaml` dos componentes | Componentes visuais do dashboard | Desktop / apresentação |
| `Views/OverlayVisual.axaml`, `.axaml.cs` | Composição visual compartilhada e rasterização Avalonia | Desktop / apresentação |
| `Views/NativeOverlay.cs`, `Views/NativeOverlayHost.cs` | Janela Win32 e adaptador para o overlay compartilhado | Desktop / apresentação de plataforma |

As bibliotecas em `lib` são código de terceiros com alterações locais; consultar
[`lib/PATCHES.md`](../../lib/PATCHES.md) e as licenças antes de modificá-las.
Não foram alteradas nesta etapa.
