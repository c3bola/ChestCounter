# Processamento de replays

`Program` carrega configuração e histórico por `Store`, instancia `Tracker` e
`Monitor` e inicia a interface. `Monitor` observa a pasta e solicita varreduras.
`Tracker` identifica arquivos `.replay`, evita reprocessamento por chave do
arquivo e aguarda estabilidade ou finalização antes de analisar. O leitor
`ReplayAnalyzer` usa `FortniteReplayReader` e `Unreal.Core` para interpretar
eventos, associar jogadores e extrair contêineres e tesouros.

Tesouros são classificados como próprios, da equipe, de outros jogadores
observados ou sem atribuição. O relatório mantém todos os escopos; a opção de
somar tesouros da equipe move somente as recompensas da equipe para a contagem
exibida, sem alterar o histórico persistido. O alerta de token pirata exige um
identificador ou nome de prêmio semanticamente explícito; IDs desconhecidos
não são inferidos como tokens. Cartazes Se Busca e os cofres associados têm
classificações distintas.

As partidas e arquivos processados são persistidos em `data.json`; configuração
em `config.json` e nomes de tesouros em `treasures.json`. `Profiles.View` aplica
o perfil e as opções existentes antes da criação do estado para o dashboard,
relatório e overlay. `Monitor.Publish` distribui as atualizações.

Somente playlists Battle Royale reconhecidas entram nas estatísticas. A
classificação usa os identificadores de playlist, não a mera presença de um
contêiner no replay.

Replays em andamento podem não ser legíveis enquanto a chave de criptografia
ainda não foi gravada. As adaptações locais, inclusive leitura parcial e
tratamento da chave, estão descritas em [`lib/PATCHES.md`](../../lib/PATCHES.md).
Não se deve atualizar essas bibliotecas como substituição cega: os patches são
utilizados pelo analisador. Regras de contagem e formatos persistidos não foram
alterados pela configuração de escala do overlay.
