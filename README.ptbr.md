# Fortnite Chest Counter

[![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-3a7bd5?style=flat-square)](https://www.microsoft.com/windows) [![.NET 10](https://img.shields.io/badge/.NET-10-512bd4?style=flat-square)](https://dotnet.microsoft.com/) [![licença](LICENSE)](LICENSE)

**Conte os baús e tesouros que você abre no Fortnite, partida após partida.**

</div>

## Visão geral

O ChestCounter é um aplicativo desktop para Windows que analisa os arquivos de replay do Fortnite para contabilizar baús e recompensas da caça ao tesouro. Ele mantém um histórico local das partidas e oferece estatísticas, relatórios, overlay sobre o jogo e uma fonte de navegador para OBS.

O aplicativo é desenvolvido em **C#/.NET 10 com Avalonia UI**. A leitura, descompressão e análise dos replays são realizadas pelas bibliotecas relacionadas incluídas no repositório.

- **Dashboard:** totais, partida recente, estatísticas da sessão, recordes, tesouros, gráfico de sete dias e estatísticas dos companheiros.
- **Relatórios:** visualização no navegador com resumo, baús, tesouros, partidas e companheiros.
- **Overlay:** exibição em dois painéis para contagem de baús e tesouros, disponível no aplicativo e por uma fonte local de navegador para OBS.
- **Perfis:** estatísticas separadas para os jogadores identificados nos replays quando mais de uma conta Fortnite utiliza o mesmo PC.
- **Dados locais:** histórico de partidas e configurações ficam armazenados no computador.
- **Foco em privacidade:** o aplicativo analisa arquivos de replay; não lê nem modifica a memória do processo do Fortnite.

> O ChestCounter contabiliza o que pode ser identificado nos replays. Ele não mostra a localização de baús ainda não abertos no mapa.

## Primeiros passos

1. Baixe a versão mais recente disponível na página de [Releases do GitHub](../../releases/latest).
2. No Fortnite, ative **Configurações → Jogo → Replays → Gravar replays**. Ative também a opção de replay dos modos de equipes grandes, caso jogue esses modos.
3. Execute o ChestCounter.
4. Na primeira inicialização, escolha se deseja importar os replays que já estão no computador ou começar a contabilizar somente as próximas partidas.
5. Mantenha o ChestCounter aberto enquanto joga. Você pode minimizar a janela.

A análise depende de o Fortnite gravar a partida. Partidas jogadas enquanto o ChestCounter estiver fechado poderão ser importadas na próxima inicialização, desde que o Fortnite ainda não tenha apagado os respectivos arquivos de replay.

## Dashboard

O dashboard reúne as principais estatísticas:

- Total de baús e tesouros.
- Partida atual ou mais recente.
- Estatísticas da sessão atual.
- Recordes diários e por partida.
- Média de baús por partida.
- Gráfico dos últimos sete dias.
- Estatísticas dos companheiros e da equipe, quando habilitadas.

A janela do aplicativo pode ser redimensionada. A interface se adapta ao espaço disponível.

## Relatórios

O botão **Relatório** abre o perfil correspondente na FortCards no navegador padrão. Se o perfil não puder ser identificado com segurança ou a API estiver indisponível, ele abre a lista geral de jogadores.

O relatório local é atualizado automaticamente enquanto o aplicativo está em execução.

| Seção | Informações |
|---|---|
| **Visão geral** | Totais, sessão, vitórias, colocações no top, quantidade de partidas, médias, recordes, tesouros, gráfico de 30 dias e partidas recentes. |
| **Baús** | Contagem por tipo de baú, contêineres adicionais compatíveis e modo de jogo. |
| **Tesouros** | Recompensas da caça ao tesouro, origem e momento registrado, quando disponível. |
| **Partidas** | Detalhes das partidas recentes, incluindo modo, colocação, eliminações, baús e tesouros. |
| **Companheiros** | Totais da equipe, ranking de companheiros e estatísticas por partida. |

O relatório acompanha o idioma selecionado no aplicativo e se adapta a telas menores.

## Companheiros e equipe

O acompanhamento de companheiros pode ser habilitado nas configurações.

- O ChestCounter identifica companheiros e partidas disputadas juntos a partir dos dados dos replays.
- A contagem de baús dos companheiros é **aproximada**. Os replays do Fortnite registram apenas atividades dentro de uma distância limitada do jogador local; atividades fora desse alcance podem não ser gravadas.
- Os totais da equipe e as estatísticas por partida podem ser exibidos separadamente dos seus totais pessoais.
- Uma opção permite incluir os baús observados dos companheiros no seu total geral.

Por isso, quando o replay não contém toda a atividade de um companheiro, a contagem deve ser interpretada como um valor mínimo.

## Overlay e OBS

O overlay exibe dois painéis integrados: um para os baús abertos e outro para os tesouros.

O overlay do aplicativo e a fonte de navegador do OBS utilizam a mesma apresentação visual e os mesmos dados de contagem. Quando desbloqueado, o overlay pode ser redimensionado proporcionalmente arrastando seu canto inferior direito. A escala também pode ser ajustada nas configurações.

Para visualizar o overlay sobre o jogo, configure o modo de exibição do Fortnite como **Tela cheia em janela**. O Windows não consegue desenhar o overlay de forma confiável sobre o modo de tela cheia exclusiva.

Para utilizar o overlay no OBS:

1. Mantenha o ChestCounter em execução.
2. Abra as configurações e copie o endereço local do overlay.
3. No OBS, adicione uma **Fonte de navegador** e informe o endereço.
4. Mantenha o fundo da fonte de navegador transparente.

A página local do overlay é servida pelo próprio ChestCounter; não é necessário utilizar um serviço externo de hospedagem.

### Atalhos de teclado

| Atalho | Ação |
|---|---|
| **Ctrl+Shift+O** | Exibir ou ocultar o overlay. |
| **Ctrl+Shift+L** | Bloquear ou desbloquear o overlay. |
| **Clique com o botão direito no overlay** | Ocultar o overlay. |

Quando bloqueado, o overlay não se move e os cliques do mouse passam através dele. Quando desbloqueado, pode ser arrastado e redimensionado.

## Configurações

As configurações incluem as opções disponíveis na versão atual do aplicativo, como:

- Idioma do aplicativo.
- Comportamento de inicialização junto com o Fortnite.
- Abertura automática do relatório.
- Acompanhamento de companheiros e inclusão dos baús observados no total.
- Contabilização de contêineres adicionais.
- Ajuste inicial da contagem de baús.
- Preferências de atualização.
- Seleção da pasta de replays.
- Exibição, bloqueio e escala do overlay.
- Redefinição das estatísticas, com criação de backup.

As opções disponíveis podem mudar entre versões.

## Como funciona

O Fortnite pode salvar um arquivo de replay de cada partida. O ChestCounter processa esses arquivos para extrair eventos e estatísticas.

- **Baseado em replays:** lê arquivos `.replay` da pasta configurada (por padrão, `%LOCALAPPDATA%\FortniteGame\Saved\Demos`).
- **Sem acesso à memória:** não lê, grava ou injeta dados na memória do processo do Fortnite.
- **Sem modificar o jogo:** não altera os arquivos da instalação do Fortnite.
- **Dados locais:** o histórico de partidas e as configurações são armazenados localmente.
- **Momento da análise:** algumas informações do replay só ficam totalmente disponíveis depois que a partida termina. Por isso, a partida pode ser finalizada após o replay estar completo.

Dependendo das configurações de atualização, o ChestCounter pode acessar o GitHub para verificar se existe uma nova versão do aplicativo.

## Contabilização de tesouros

O ChestCounter identifica recompensas da caça ao tesouro utilizando as informações disponíveis nos replays e os dados locais de nomes de tesouros.

O replay pode fornecer um identificador da recompensa em vez de seu nome. O arquivo local `treasures.json` relaciona identificadores conhecidos a nomes legíveis. Algumas recompensas podem permanecer sem identificação quando as informações necessárias não estiverem disponíveis.

## Perfis

Quando existem replays de várias contas Fortnite no mesmo computador, o ChestCounter pode manter estatísticas separadas por perfil. O perfil é identificado a partir dos dados do replay; não é necessário informar manualmente o nome do jogador.

## Atualizações

O comportamento das atualizações é controlado nas configurações do aplicativo. Conforme a preferência selecionada, o ChestCounter pode verificar novas versões e aplicar a atualização ou solicitar confirmação antes.

Consulte a página de [Releases](../../releases) para ver as versões publicadas e suas notas.

## Dados locais e backups

O ChestCounter armazena os dados do aplicativo no diretório de dados do usuário. O local padrão é:

```text
%APPDATA%\ChestCounter
```

Os arquivos mantidos nesse diretório podem variar conforme a versão. Faça backup do diretório de dados antes de migrar para outro computador ou realizar manutenção manual. Para começar um novo histórico, utilize a opção de redefinição de estatísticas do aplicativo; essa operação cria um backup.

## Opções de linha de comando

O aplicativo oferece opções de linha de comando para tarefas como selecionar outra pasta de replays ou de dados, analisar uma vez os replays concluídos, reiniciar a configuração inicial e gerar uma prévia do overlay.

Para consultar os nomes exatos dos parâmetros e seu comportamento na versão utilizada, verifique o tratamento dos argumentos de linha de comando no código-fonte correspondente.

## Compilação a partir do código-fonte

### Requisitos

- Windows 10 ou Windows 11 (x64).
- .NET 10 SDK.
- Git.

### Clonar o repositório

```bash
git clone https://github.com/c3bola/ChestCounter.git
cd ChestCounter
```

Abra a solução em uma IDE compatível com .NET 10 e Avalonia ou compile a solução utilizando a CLI do .NET. Os arquivos de solução e projetos presentes no repositório são a referência para a estrutura de compilação vigente.

> Os scripts de compilação e empacotamento podem mudar conforme o projeto evolui. Consulte o repositório antes de utilizar instruções ou caminhos de versões antigas.

## Arquitetura

O código é organizado em camadas de aplicação e uma camada de apresentação desktop. A separação de responsabilidades esperada é:

- **Core/domínio:** modelos e regras fundamentais, independentes da interface desktop.
- **Application:** casos de uso e contratos da aplicação.
- **Infrastructure:** processamento de replays, persistência, integração com o sistema operacional e serviços externos.
- **Desktop:** interface Avalonia, views, view models e composição da aplicação.

Os nomes exatos dos projetos e diretórios são definidos pela solução atual. As dependências devem apontar para dentro: a lógica de domínio não deve depender do Avalonia nem de componentes específicos da interface desktop.

## Contribuições e relatos de problemas

Relatos de bugs e sugestões são bem-vindos por meio do [GitHub Issues](../../issues).

Ao relatar um problema na leitura de replay, inclua o log relevante do aplicativo (por exemplo, `errors.log`, caso exista) e informe o modo de jogo e a data aproximada da partida. Não publique dados pessoais ou arquivos de replay publicamente se não estiver confortável em compartilhar seu conteúdo.

## Licença e créditos

Este projeto é distribuído sob a [licença MIT](LICENSE).

A leitura e a descompressão dos replays utilizam bibliotecas de terceiros incluídas no repositório. Consulte os avisos, licenças e notas de alterações nos diretórios das respectivas bibliotecas (incluindo [`lib/PATCHES.md`](lib/PATCHES.md), caso exista).

Este projeto não é afiliado, endossado ou patrocinado pela Epic Games. Fortnite é uma marca registrada da Epic Games, Inc.
