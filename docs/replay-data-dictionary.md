# Dicionário de dados de replay

## Escopo e regras de leitura

O analisador preserva dados internos em `MatchRecord.RawReplayData`. O objeto
contém `parseMode` e as seções `match`, `players`, `teams`,
`eventEliminations`, `killFeed`, `map`, `weapons` e `observations`. Os valores
são serializados a partir dos modelos e eventos que o parser efetivamente
produziu; campos não observados permanecem nulos ou ausentes. Eventos de fontes
distintas permanecem separados.

Esse snapshot é salvo localmente. **Ele ainda não é enviado à API**: o mapper e
o JSON de ingestão existentes não foram alterados enquanto o contrato
`reference/openapi.yaml` não estiver disponível e auditado. Portanto, “API:
pendente” em todas as tabelas significa que a aceitação e os nomes finais dos
campos ainda precisam ser validados com o OpenAPI.

Status usados:

- **Disponível:** valor recebido e decodificado do replay, quando presente.
- **Condicional:** depende do conteúdo do replay, da atualização replicada ou
  do `ParseMode`.
- **Derivado:** resultado de interpretação ou cálculo que deve ser feito
  posteriormente pela API; o aplicativo não o calcula como regra de negócio.
- **Não disponível:** não há evidência suficiente no replay ou o parser atual
  não encaminha o dado para o resultado.

| `ParseMode` | O que o parser lê | Limitação relevante |
|---|---|---|
| `EventsOnly` | Eventos de texto do replay, como Stats, TeamStats e eliminações. | Não lê os blocos de rede usados para jogadores, equipes, mapa, movimento e interações. |
| `Minimal` | Eventos e grupos de rede marcados como `Minimal`. | Não inclui propriedades/grupos marcados `Normal` ou `Full`. |
| `Normal` | Grupos `Minimal` e `Normal`; é o padrão atual do analisador. | `PlayerPawn.ReplicatedMovement` e `FortPoiManager` exigem `Full`. |
| `Full` | Grupos e propriedades marcados até `Full`. | Aumenta o volume de dados e o custo de parsing; grupos `Debug` e `Ignore` continuam fora. |

`CHESTCOUNTER_MODE` permite selecionar um modo sem mudar o padrão. Grupos
podem ser ignorados seletivamente pelo novo parâmetro `ignoredExportGroups`.
Os veículos específicos atualmente marcados como `Ignore` não são decodificados
em nenhum dos quatro modos acima.

## 1. Partida

| Campo | Tipo | Origem | Modo / evidência / status | Limitações e uso posterior |
|---|---|---|---|---|
| `match.gameData.gameSessionId` | string? | `GameData.GameSessionId` ← `GameState.GameSessionId` | `Minimal`; **Condicional** | Pode estar ausente em replay antigo ou sem a atualização. API: pendente. |
| `match.gameData.utcTimeStartedMatch` | DateTime? | `GameData.UtcTimeStartedMatch` ← `GameState.UtcTimeStartedMatch.Time` | `Minimal`; **Condicional** | Timestamp fornecido pelo replay. Não é calculada duração. API: pendente. |
| `match.gameData.matchEndTime` | float? | `GameData.MatchEndTime` ← `GameState.EndGameStartTime` | `Minimal`; **Condicional** | Valor de tempo replicado, não um DateTime de término. API pode interpretá-lo com outros relógios. |
| `match.replayTimestamp` | DateTime? | `Replay.Info.Timestamp` | Eventos/arquivo; **Condicional** | Timestamp do replay, não necessariamente o início real da partida. |
| `match.replayLengthInMilliseconds` | int? | `Replay.Info.LengthInMs` | Cabeçalho; **Condicional** | Tamanho temporal declarado pelo replay; não é duração individual calculada. |
| `match.replayBranch` | string? | `Replay.Header.Branch` | Cabeçalho; **Condicional** | Branch/build reportado no cabeçalho. |
| `match.gameData.currentPlaylist` | string? | `GameData.CurrentPlaylist` ← `PlaylistInfo.Name` | `Minimal`; **Condicional** | Identificador recebido; o app mantém a classificação de playlist usada pelas regras atuais. |
| `match.gameData.mapInfo` | string? | `GameData.MapInfo` ← `GameState.MapInfo.Name` | `Minimal`; **Condicional** | Nome depende da definição enviada pelo replay. |
| `match.gameData.additionalPlaylistLevels` | string[]? | `GameData.AdditionalPlaylistLevels` ← `GameState.AdditionalPlaylistLevelsStreamed` | `Minimal`; **Condicional** | Lista recebida; não identifica por si só POIs. |
| `match.gameData.activeGameplayModifiers` | string[] | `GameData.ActiveGameplayModifiers` ← `ActiveGameplayModifier.ModifierDef.Name` | `Minimal`; **Condicional** | Só inclui modificadores observados. |
| `match.gameData.recorderId` | uint? | `GameData.RecorderId` ← `GameState.RecorderPlayerState` | `Minimal`; **Condicional** | GUID de ator, não Epic ID. A biblioteca foi atualizada para marcar o dono mesmo quando esse estado chega depois dos PlayerStates. |
| `match.gameData.teamCount` | int? | `GameData.TeamCount` ← `GameState.TeamCount` | `Minimal`; **Condicional** | Campo mantido com o nome recebido. Não é tratado como quantidade de jogadores. |
| `match.gameData.teamSize` | int? | `GameData.TeamSize` ← `GameState.TeamSize` | `Minimal`; **Condicional** | Não é preenchido usando o tamanho de outro array. |
| `match.gameData.playerBotsLeft` | int? | `GameData.PlayerBotsLeft` ← `GameState.PlayerBotsLeft` | `Minimal`; **Condicional** | Valor de bots restantes observado; não equivale a bots que iniciaram a partida. |
| `match.gameData.playersLeft` | int? | `GameData.PlayersLeft` ← `GameState.PlayersLeft` | `Minimal`; **Condicional** | Estado observado, não quantidade inicial. |
| `match.gameData.teamsLeft` | int? | `GameData.TeamsLeft` ← `GameState.TeamsLeft` | `Minimal`; **Condicional** | Estado observado, não quantidade inicial. |
| `match.gameData.elapsedTime` | int? | `GameData.ElapsedTime` ← `GameState.ElapsedTime` | `Minimal`; **Condicional** | Valor reportado pelo jogo; não é calculado pelo app. |
| `match.gameData.gamePhase` | int? | `GameData.GamePhase` ← `GameState.GamePhase` | `Minimal`; **Condicional** | Enum numérico recebido. |
| `match.gameData.safeZonePhase` | byte? | `GameData.SafeZonePhase` ← `GameState.SafeZonePhase` | `Minimal`; **Condicional** | Enum/estado numérico recebido. |
| `match.gameData.hasBegunPlay` | bool? | `GameData.HasBegunPlay` ← `GameState.bReplicatedHasBegunPlay` | `Minimal`; **Condicional** | Evidência do estado replicado; não determina sozinha quem efetivamente iniciou. |
| `match.gameData.gameStateObservations[]` | objetos | `GameData.GameStateObservations` ← atualizações de `GameState` | `Minimal`; **Condicional** | Preserva relógio, TeamCount, TeamSize, bots/jogadores/equipes restantes, tempo decorrido e fases quando recebidos. Repetições idênticas consecutivas são omitidas; não é um log de cada pacote. |
| `match.gameData.maxPlayers` | int? | `GameData.MaxPlayers` | Propriedade legada não preenchida; **Não disponível** | `GameState.TeamCount` não é copiado para esse campo. Não usar como quantidade inicial. |
| `match.gameData.totalTeams` | int? | `GameData.TotalTeams` | Propriedade sem atribuição no builder atual; **Não disponível** | Não inferir pelo número de equipes observadas. |
| `match.gameData.totalBots` | int? | `GameData.TotalBots` | Propriedade legada não preenchida; **Não disponível** | `PlayerBotsLeft` não é promovido a total de bots. |
| `match.gameData.totalPlayerStructures` | int? | `GameData.TotalPlayerStructures` ← `GameState.TotalPlayerStructures` | `Minimal`; **Condicional** | Valor do jogo, se replicado. |
| `match.gameData.winningTeam` | uint? | `GameData.WinningTeam` ← `GameState.WinningTeam` | `Minimal`; **Condicional** | Identificador fornecido pelo replay. |
| `match.gameData.winningPlayerIds` | int[]? | `GameData.WinningPlayerIds` ← `GameState.WinningPlayerList` | `Minimal`; **Condicional** | Lista do jogo; não inferida por colocação. |
| `match.gameData.aircraftStartTime` / `safeZonesStartTime` | float? | `GameData.AircraftStartTime` / `SafeZonesStartTime` ← `GameState` | `Minimal`; **Condicional** | Tempos brutos reportados. |
| `match.stats.*` | objeto | `FortniteReplay.Stats` / `Stats` do evento `MATCH_STATS` | `EventsOnly` ou superior; **Condicional** | Inclui Accuracy, Assists, Eliminations, dano, Revives, materiais e `TotalTraveled` fornecidos no evento. `DamageToPlayers` é propriedade derivada da biblioteca e não é incluída no snapshot. |
| `match.teamStats.position` / `totalPlayers` | uint | `FortniteReplay.TeamStats.Position` / `TotalPlayers` do evento `TEAM_STATS` | `EventsOnly` ou superior; **Condicional** | Valores do evento; `totalPlayers` não prova quantos jogadores iniciaram. |

## 2. Jogadores

Fonte principal: `FortniteReplay.PlayerData[]` / `PlayerData`, incluindo as
propriedades listadas abaixo. Os valores podem ser nulos quando o replay não
replicou a propriedade.

| Campo | Tipo | Origem | Modo / evidência / status | Limitações e uso posterior |
|---|---|---|---|---|
| `players[].id` | int? | `PlayerData.Id` ← `FortPlayerState.PlayerId` ou `PlayerID` | `Minimal`; **Condicional** | ID numérico do replay, não Epic ID. |
| `players[].playerId` | string? | `PlayerData.PlayerId` | `Minimal`; **Derivado** | Getter da biblioteca: BotId para bot; senão EpicId ou PlatformUniqueNetId. |
| `players[].epicId` / `platformUniqueNetId` / `botId` | string? | `PlayerData.EpicId` / `PlatformUniqueNetId` / `BotId` ← `FortPlayerState` | `Minimal`; **Condicional** | Identificadores só existem quando enviados. |
| `players[].isBot` | bool | `PlayerData.IsBot` ← `FortPlayerState.bIsABot` | `Minimal`; **Condicional** | Não inferir bot por nome ou ID. |
| `players[].playerName` | string? | `PlayerData.PlayerName` ← `PlayerNameData.DecodedName` | `Minimal`; **Condicional** | Nome privado pode faltar ou estar anonimizado. |
| `players[].playerNameCustomOverride` / `streamerModeName` | string? | `PlayerData` ← `FortPlayerState` | `Minimal`; **Condicional** | Textos recebidos; podem substituir/ocultar o nome usual. |
| `players[].platform` / `level` / `seasonLevelUIDisplay` | string? / int? / uint? | `PlayerData` ← `FortPlayerState` | `Minimal`; **Condicional** | Metadados do participante reportados pelo jogo. |
| `players[].playerNumber` | int? | `PlayerData.PlayerNumber` ← `FortPlayerState.WorldPlayerId` | `Minimal`; **Condicional** | Número do mundo, não posição final. |
| `players[].teamIndex` | int? | `PlayerData.TeamIndex` ← `FortPlayerState.TeamIndex` | `Minimal`; **Condicional** | Associação disponível somente quando recebida. |
| `players[].isPartyLeader` / `isReplayOwner` | bool | `PlayerData` | `Minimal`; **Condicional** | ReplayOwner é associado pelo RecorderId do jogo. |
| `players[].isGameSessionOwner` | bool? | `PlayerData.IsGameSessionOwner` | `Minimal`; **Condicional** | Flag replicada. |
| `players[].hasFinishedLoading` / `hasStartedPlaying` | bool? | `PlayerData` ← `FortPlayerState` | `Minimal`; **Condicional** | Evidências úteis, mas não garantem sozinhas contagem exata de jogadores no início. |
| `players[].hasThankedBusDriver` / `isUsingStreamerMode` / `isUsingAnonymousMode` | bool? | `PlayerData` ← `FortPlayerState` | `Minimal`; **Condicional** | Flags do estado do jogador. |
| `players[].disconnected` | bool? | `PlayerData.Disconnected` ← `FortPlayerState.bIsDisconnected` | `Minimal`; **Condicional** | Último estado conhecido, não histórico completo de conexão. |
| `players[].rebootCounter` / `placement` / `kills` / `teamKills` | uint? / int? / uint? / uint? | `PlayerData` ← `FortPlayerState` | `Minimal`; **Condicional** | Valores recebidos do jogo; não agregados pelo ChestCounter. |
| `players[].deathCause` / `deathCircumstance` / `deathTags` / `deathLocation` | int? / int? / string[]? / FVector? | `PlayerData` ← `FortPlayerState` | `Minimal`; **Condicional** | Dependem da atualização de morte/estado do jogador. |
| `players[].deathTime` / `deathTimeDouble` | float? / double? | `PlayerData` ← relógio de mundo quando `DeathTags` é observado | `Minimal`; **Condicional** | Relógio replicado associado à atualização; não é duração calculada. |
| `players[].cosmetics` | objeto | `PlayerData.Cosmetics` ← `FortPlayerState` e `PlayerPawn` | `Minimal`/`Normal`; **Condicional** | Inclui personagem, partes, gênero/tipo, banner, mochila, glider, pickaxe, wraps, contrail, dances e outros campos do modelo. |
| `players[].currentWeapon` | uint? | `PlayerData.CurrentWeapon` ← `PlayerPawn.CurrentWeapon` | `Minimal`; **Condicional** | Referência de ator, não nome de arma. |
| `players[].inventoryId` | uint? | `PlayerData.InventoryId` | Grupo declarado, callback de inventário desligado; **Não disponível** no resultado atual | O modelo pode existir, mas o reader não associa itens a esse campo. |

## 3. Equipes

| Campo | Tipo | Origem | Modo / evidência / status | Limitações e uso posterior |
|---|---|---|---|---|
| `teams[].teamIndex` | int? | `TeamData.TeamIndex` ← agrupamento por `PlayerData.TeamIndex` | `Minimal`; **Condicional** | Grupo só é construído para jogadores com TeamIndex. |
| `teams[].playerIds` | int?[] | `TeamData.PlayerIds` | `Minimal`; **Condicional** | IDs numéricos conhecidos pelo replay; pode não incluir participantes nunca observados. |
| `teams[].playerNames` | string?[] | `TeamData.PlayerNames` | `Minimal`; **Condicional** | Nome recebido ou identificador alternativo usado pela biblioteca. |
| `teams[].partyOwnerId` | int? | `TeamData.PartyOwnerId` | `Minimal`; **Condicional** | Só preenchido quando o líder de grupo é identificado. |
| `teams[].placement` / `teamKills` | int? / uint? | `TeamData` derivado dos valores dos `PlayerData` da equipe | `Minimal`; **Condicional** | Valor do membro observado; não constitui uma nova contagem do app. |

## 4. Identificação de bots

| Campo | Tipo | Origem | Modo / evidência / status | Limitações e uso posterior |
|---|---|---|---|---|
| `players[].isBot` / `botId` | bool / string? | `PlayerData` ← `FortPlayerState.bIsABot` / `BotUniqueId` | `Minimal`; **Condicional** | São os sinais explícitos de bot disponíveis no estado. |
| `eventEliminations[].eliminated.playerType` / `eliminator.playerType` | enum | `PlayerEliminationInfo.PlayerType` | Eventos; **Condicional** | Distingue player, bot e named bot conforme o evento. |
| `killFeed[].playerIsBot` / `finisherOrDownerIsBot` | bool | `KillFeedEntry` | `Minimal`; **Condicional** | Flags da entrada de kill feed. |
| Quantidade de bots que iniciaram | — | Sem campo inicial comprovado | —; **Não disponível** | `PlayerBotsLeft`, registros observados e `TotalBots` não são tratados como total inicial. |

## 5. Eliminações

As fontes são preservadas separadamente. `eventEliminations[]` vem dos eventos
customizados; `killFeed[]` vem das atualizações replicadas de PlayerState.
Não há consolidação nem soma dessas fontes. Entradas repetidas idênticas são
removidas dentro de cada fonte quando a identidade do evento pode ser comparada
pelos valores exatos.

| Campo | Tipo | Origem | Modo / evidência / status | Limitações e uso posterior |
|---|---|---|---|---|
| `eventEliminations[].eventInfo` | objeto | `PlayerElimination.Info` / `EventInfo` (`Group`, `Metadata`, `StartTime`, `EndTime`, `SizeInBytes`) | Eventos; **Condicional** | `StartTime`/`EndTime` são os tempos brutos do evento. |
| `eventEliminations[].eliminated.id` / `eliminator.id` | string? | `PlayerEliminationInfo.Id` | Eventos; **Condicional** | IDs usam o formato definido pela versão do evento; não associar por proximidade temporal. |
| `eventEliminations[].eliminated.playerType` / `eliminator.playerType` | enum | `PlayerEliminationInfo.PlayerType` | Eventos; **Condicional** | Pode distinguir humanos e tipos de bot. |
| `eventEliminations[].eliminated.location` / `eliminator.location` | FVector | `PlayerEliminationInfo.Location` | Eventos; **Condicional** | Transform só é lido em determinadas versões do evento. |
| `eventEliminations[].eliminated.rotation` / `eliminator.rotation` / `scale` | FQuat / FVector | `PlayerEliminationInfo` | Eventos; **Condicional** | Pode estar zerado ou ausente em eventos antigos. |
| `eventEliminations[].gunType` / `knocked` | byte / bool | `PlayerElimination.GunType` / `Knocked` | Eventos; **Condicional** | `knocked` não equivale a eliminação definitiva. |
| `eventEliminations[].libraryDistance` | double? | `PlayerElimination.Distance` | Eventos; **Derivado** | Distância calculada pela biblioteca entre coordenadas. ChestCounter não a recalcula; a API pode preferir calcular de coordenadas confiáveis. |
| `killFeed[].playerId` / `playerName` / `playerIsBot` | int? / string? / bool | `KillFeedEntry` | `Minimal`; **Condicional** | Participante afetado, quando o estado o identifica. |
| `killFeed[].finisherOrDowner` / `finisherOrDownerName` / `finisherOrDownerIsBot` | int? / string? / bool | `KillFeedEntry` ← `FortPlayerState.FinisherOrDowner` | `Minimal`; **Condicional** | A referência pode não resolver para um PlayerData conhecido. |
| `killFeed[].replicatedWorldTimeSeconds` / `replicatedWorldTimeSecondsDouble` | float? / double? | `KillFeedEntry` | `Minimal`; **Condicional** | Relógios de mundo observados no momento da atualização. |
| `killFeed[].distance` / `deathCause` / `deathCircumstance` / `deathLocation` / `deathTags` | float? / int? / int? / FVector / string[]? | `KillFeedEntry` ← `FortPlayerState` | `Minimal`; **Condicional** | Distância é fornecida pelo estado; coordenadas/tags dependem da atualização. |
| `killFeed[].isDowned` / `isRevived` | bool / bool | `KillFeedEntry` | `Minimal`; **Condicional** | Down/knock e reboot/revive são estados distintos. Eventos repetidos idênticos da mesma fonte são deduplicados; não são mesclados com `eventEliminations`. |
| Número total consolidado de eliminações individuais | — | Não criado pelo snapshot bruto | —; **Derivado** | API deve decidir a fonte e evitar contar duas vezes um evento representado em mais de uma fonte. |

## 6. Movimentação

Os dados ficam em `players[].locations[]` (`PlayerData.Locations` /
`PlayerMovement`). O builder cria uma amostra quando recebe `ReplicatedMovement`.
Esse campo está marcado `ParseMode.Full`; portanto, `Normal` não fornece esses
snapshots. As amostras são atualizações replicadas, não uma trilha contínua.

| Campo | Tipo | Origem | Modo / evidência / status | Limitações e uso posterior |
|---|---|---|---|---|
| `locations[].pawnActorId` | uint? | `PlayerMovement.PawnActorId` ← GUID do canal do PlayerPawn | `Full`; **Condicional** | Liga a amostra ao ator observado; pode faltar se o canal não resolver. |
| `locations[].replicatedWorldTimeSeconds` / `replicatedWorldTimeSecondsDouble` | float? / double? | `PlayerMovement` ← relógios de `GameState` | `Full`; **Condicional** | Permanecem nulos até que o relógio seja observado; zero não é inventado pelo builder. |
| `locations[].lastUpdateTime` | float? | `PlayerMovement.LastUpdateTime` ← `PlayerPawn.ReplayLastTransformUpdateTimeStamp` | `Full`; **Condicional** | Timestamp replicado pela pawn. |
| `locations[].replicatedMovement.location` | FVector | `FRepMovement.Location` | `Full`; **Condicional** | Posição da pawn no snapshot; não garante posição de ator/veículo separado. |
| `locations[].replicatedMovement.rotation` | FRotator | `FRepMovement.Rotation` | `Full`; **Condicional** | Rotação replicada. |
| `locations[].replicatedMovement.linearVelocity` / `angularVelocity` | FVector | `FRepMovement` | `Full`; **Condicional** | Velocidades replicadas, com quantização do replay. |
| `locations[].replicatedMovement.acceleration` | FVector? | `FRepMovement.Acceleration` | `Full`; **Condicional** | Só existe quando o replay sinaliza aceleração replicada. |
| `locations[].replicatedMovement` flags e quantização | bools, uints, enums | `FRepMovement` (`bSimulatedPhysicSleep`, `bRepPhysics`, `bRepAcceleration`, `ServerFrame`, handles e níveis de quantização) | `Full`; **Condicional** | Metadados brutos da estrutura. |
| `locations[].replicatedMovementMode` / `currentMovementStyle` | int? / int? | `PlayerMovement` ← `PlayerPawn` | `Full`; **Condicional** | Enums/códigos numéricos; a biblioteca não os converte em rótulos de negócio. |
| `locations[].bIsSprinting` / `bIsJumping` / `bIsCrouched` / `bIsSlopeSliding` | bool? | `PlayerMovement` ← `PlayerPawn` | `Full`; **Condicional** | Flags independentes conforme recebidas. |
| `locations[].bIsZiplining` / `bIsTargeting` / `bIsDBNO` / `bIsHonking` / `bIsInAnyStorm` | bool? | `PlayerMovement` ← `PlayerPawn` | `Full`; **Condicional** | Flags conforme recebidas. |
| `locations[].bIsWaitingForEmoteInteraction` / `bIsPlayingEmote` | bool? | `PlayerMovement` ← `PlayerPawn` | `Full`; **Condicional** | Flags conforme recebidas. |
| `locations[].bIsSkydiving` / `bIsSkydivingFromLaunchPad` / `bIsSkydivingFromBus` | bool? | `PlayerMovement` ← `PlayerPawn` | `Full`; **Condicional** | Flags conforme recebidas. |
| `locations[].bIsParachuteOpen` / `bIsParachuteForcedOpen` / `bIsInWaterVolume` | bool? | `PlayerMovement` ← `PlayerPawn` | `Full`; **Condicional** | Flags conforme recebidas. |
| `bIsWalking` / distância percorrida | — | Não há campo de caminhada nem cálculo no app | —; **Não disponível** / **Derivado** | Não inferir caminhada por ausência de sprint. A API pode interpretar amostras, se houver evidência suficiente. |

## 7. Veículos

| Campo | Tipo | Origem | Modo / evidência / status | Limitações e uso posterior |
|---|---|---|---|---|
| `locations[].vehicle` | uint? | `PlayerMovement.Vehicle` ← `PlayerPawn.Vehicle` | Snapshot com `Full`; **Condicional** | Referência de ator associada à pawn; a propriedade pode não ser replicada em toda amostra. |
| `locations[].vehiclePath` | string? | Resolução de `PlayerPawn.Vehicle` pelo `NetGuidCache` | `Full`; **Condicional** | Caminho só quando a referência resolve; não é uma classificação normalizada de veículo. |
| `locations[].seatIndex` | byte? | `PlayerMovement.SeatIndex` ← `PlayerPawn.SeatIndex` | `Full`; **Condicional** | Assento só quando enviado. |
| Jogador associado | int? / GUID | `players[].id`, `locations[].pawnActorId` e `vehicle` | `Full`; **Condicional** | Relação baseada em referências replicadas, sem associação por proximidade temporal. |
| Entrada/saída e horário | — | Sem evento de entrada/saída específico no modelo atual | —; **Derivado** | A API pode interpretar mudanças explícitas entre snapshots; ausência de referência não prova saída. |
| Posição exclusiva do ator veículo e classe normalizada | — | Grupos `BaseVehicle`/assentos estão `ParseMode.Ignore` | —; **Não disponível** | A posição capturada é a da pawn. O caminho resolvido pode ajudar a API, mas não comprova tipo/classe. |
| Distância em veículo | — | Não calculada | —; **Derivado** | Cabe à API, usando amostras e associações confiáveis. |

## 8. POIs e mapa

| Campo | Tipo | Origem | Modo / evidência / status | Limitações e uso posterior |
|---|---|---|---|---|
| `map.battleBusFlightPaths[]` | objetos | `MapData.BattleBusFlightPaths` ← `GameState.TeamFlightPaths` / `BattleBus` | `Minimal`; **Condicional** | Inclui índice, posição/rotação inicial, velocidade, tempos de voo/drop e skin quando recebidos. |
| `map.worldGridStart` / `worldGridEnd` / `worldGridSpacing` / `worldGridTotalSize` | FVector2D | `MapData` ← `FortPoiManager` | `Full`; **Condicional** | Valores do grid do mapa. |
| `map.gridCountX` / `gridCountY` | int? | `MapData` ← `FortPoiManager` | `Full`; **Condicional** | Dimensões recebidas. |
| `map.poiTagContainerTable` / `poiTagContainerTableSize` | tags[]? / int? | `MapData` ← `FortPoiManager.PoiTagContainerTable` / `PoiTagContainerTableSize` | `Full`; **Condicional** | Tabela é preservada; não há resolução confiável de cada coordenada em nome de POI. |
| `map.safeZones[]` | objetos | `MapData.SafeZones` ← `SafeZoneIndicator` / `SafeZone` | Conforme grupo; **Condicional** | Inclui centros, raios e tempos de shrink quando replicados. |
| `map.llamas[]` | objetos | `MapData.Llamas` ← `SupplyDropLlama` / `Llama` | Conforme grupo; **Condicional** | Posição, destino, looted e timestamps observados. |
| `map.supplyDrops[]` | objetos | `MapData.SupplyDrops` ← `SupplyDrop` | Conforme grupo; **Condicional** | Posição de pouso, queda, balão, looted e timestamps observados. |
| `map.rebootVans[]` | objetos | `MapData.RebootVans` ← `SpawnMachineRepData` / `RebootVan` | Conforme grupo; **Condicional** | GUID/handle, posição, estado e tempos de cooldown recebidos. |
| Primeira posição de pouso / timestamp | — | Derivável de snapshots de movimento | `Full`; **Derivado** | O app não escolhe um pouso nem calcula horário aproximado. |
| Nome de POI de pouso/visita | — | Não há associação de coordenada a POI confiável no parser | —; **Não disponível** | Enviar coordenadas/tags sem inventar nomes; a API pode resolver com fonte confiável. |
| Entrada, saída, retorno e permanência em POI | — | Não há eventos de limites de visita prontos | `Full`; **Derivado** | A API pode interpretar snapshots e mapa; o app não calcula duração por POI. |

## 9. Inventário

| Campo | Tipo | Origem declarada | Evidência/status atual | Limitações |
|---|---|---|---|---|
| `Inventory.Items[]` (`Count`, `ItemDefinition`, `OrderIndex`, `Durability`, `Level`, `LoadedAmmo`, `A`–`D`) | objetos | `Inventory` / `InventoryItem`, alimentados por `FortInventory` | **Não disponível** no `FortniteReplay` final | Os callbacks `FortInventory` do reader estão desativados; a existência dos modelos não comprova extração. |
| Identificador/ator de inventário e jogador | uint? / int? | `FortInventory`, `Inventory`, `PlayerData.InventoryId` | **Não disponível** no resultado atual | Não incluir no JSON como se os itens tivessem sido lidos. |
| Quantidade inicial ou histórico de coleta | — | Sem histórico de inventário disponível | **Não disponível** | Itens observados em `droppedItems` não provam coleta ou posse final. |

## 10. Armas e equipamentos

| Campo | Tipo | Origem | Modo / evidência / status | Limitações e uso posterior |
|---|---|---|---|---|
| `weapons[].actorId` / `weaponName` | uint? / string? | `FortniteReplay.Weapons[]` / `WeaponData` ← `BaseWeapon` | Conforme grupo da arma; **Condicional** | `actorId` liga ao ator observado; lista representa objetos ainda conhecidos no fim do parse, não um inventário completo histórico. |
| `weapons[].ammoCount` / `weaponLevel` / `lastFireTimeVerified` | int? / int? / float? | `WeaponData` ← `BaseWeapon` | Conforme grupo; **Condicional** | Campos só quando recebidos; sem sequência de disparos garantida. |
| `weapons[].bIsEquippingWeapon` / `bIsReloadingWeapon` / `A`–`D` | bool? / uint? | `WeaponData` ← `BaseWeapon` | Conforme grupo; **Condicional** | Estado/valores replicados. |
| `players[].currentWeapon` | uint? | `PlayerData.CurrentWeapon` ← `PlayerPawn.CurrentWeapon` | `Minimal`; **Condicional** | Referência de ator; nome pode não resolver. |
| `observations.equippedItems[]` | evento com tempo, nome, pawn e player ID? | `ReplayAnalyzer` ← `GenericWeapon` / `OnExportRead` | `Minimal`; **Condicional** | Apenas armas/equipamentos observados pelo grupo genérico; player ID só quando as referências resolvem. |
| Historico completo de equipamento | — | Não existe sequência geral de todas as armas no modelo atual | **Não disponível** | Não extrapolar a amostra final de `weapons[]` para todo o jogo. |

## 11. Baús e contêineres

| Campo | Tipo | Origem | Modo / evidência / status | Limitações e uso posterior |
|---|---|---|---|---|
| `observations.containerInteractions[].timeSeconds` | float | `ReplayAnalyzer.Now` / tempo do frame | `Minimal` ou superior; **Condicional** | Tempo do evento observado. |
| `sourceActorKey` / `containerLabel` | string | canal/ator e archetype de contêiner | **Condicional** | `sourceActorKey` pode ser GUID/caminho ou chave de canal de fallback; label é nome limpo, não classificação oficial. |
| `searchingPawnActorId` / `searchingPlayerStateActorId` / `searchingPlayerId` | uint? / uint? / int? | `BaseContainer.SearchingPawn` e mapas de ator/estado | **Condicional** | Associação por referências explícitas; nula se a cadeia não resolve. |
| `alreadySearched` / `searchAnimationCount` / `destroyed` | bool? / uint? / bool | `BaseContainer.bAlreadySearched`, `SearchAnimationCount`, `bDestroyed` | **Condicional** | Preserva os campos dos patches do ChestCounter; eventos de baú são deduplicados pelo ator. |
| Loot Cannon | evento de interação | `LootCannonFired.InteractingPawn` | `Minimal`; **Condicional** | Registrado como interação observada, não como baú. |
| `ChestRecord.Items` | string[] | Itens de pickup associados pelo analisador a um intervalo após a interação | **Derivado** | Compatibilidade com relatório atual; associação temporal é heurística. O snapshot bruto mantém `containerInteractions` e `droppedItems` separados, sem afirmar vínculo. |
| Categoria normalizada do contêiner | string | `ChestKinds.Classify` | **Derivado** | Classificação usada pelo produto; não é nome original fornecido pelo replay. O snapshot preserva o label observado. |

## 12. Itens coletados e descartados

| Campo | Tipo | Origem | Modo / evidência / status | Limitações e uso posterior |
|---|---|---|---|---|
| `observations.droppedItems[].timeSeconds` / `item` | float / string | `FortPickup` e tempo do frame | `Normal`; **Condicional** | Inclui pickups com `bTossedFromContainer == true` e definição de item identificável. |
| `itemDefinitionActorId` / `itemDefinitionPath` | uint? / string? | `FortPickup.ItemDefinition` / `NetGuidCache` | `Normal`; **Condicional** | Resolução de caminho pode falhar. |
| `count` / `durability` / `level` / `loadedAmmo` / `orderIndex` | int? / float? / int? / int? / ushort? | `FortPickup` | `Normal`; **Condicional** | Estado do pickup, não confirmação de coleta. |
| `itemOwner` / `pawnWhoDroppedPickup` / `owner` | uint? | `FortPickup` | `Normal`; **Condicional** | Referências de ator podem não resolver para jogador conhecido. |
| `droppedByPlayerId` | int? | Resolução direta `PawnWhoDroppedPickup` → PlayerState → player ID | `Normal`; **Condicional** | Só é preenchido com associação explícita, nunca por proximidade. |
| `replicatedLocation`, `lootInitialPosition`, `lootFinalPosition`, `finalTossRestLocation` | FVector? | `FortPickup` / `FRepMovement` | `Normal`; **Condicional** | Coordenadas recebidas, não distância calculada. |
| `flyTime` / `tossState` / `pickedUp` | float? / int? / bool? | `FortPickup` | `Normal`; **Condicional** | Flags e tempos do objeto; `pickedUp` não constitui histórico completo do inventário. |
| Itens coletados por jogador / itens finais no inventário | — | Callbacks de inventário inativos | **Não disponível** | Não confundir pickup observado, item tossado ou item equipado com posse confirmada. |

## 13. Eventos especiais

| Campo | Tipo | Origem | Modo / evidência / status | Limitações e uso posterior |
|---|---|---|---|---|
| `observations.treasureEvents[].timeSeconds` / `prizeId` | float / string | `TreasureHuntChest` e leitura do texto bruto `PrizeTemplateId` | `Minimal`; **Condicional** | Exige ator/evento e texto reconhecível. |
| `treasureEvents[].pawnActorId` / `playerId` | uint? / int? | `OpeningPawn` / `Instigator` e mapa de estados | `Minimal`; **Condicional** | IDs só com referência resolvida. |
| `treasureEvents[].sourceContainer` | string | `TreasureHuntChest.SourceContainer` resolvido pelo NetGuidCache | `Minimal`; **Condicional** | Caminho pode estar vazio/não resolvido. |
| `observations.prizeHandlerGrants[]` | evento de handler, tempo, PlayerState, ID do elemento | FastArray bruto de `FortAbilitySystemComponent` via `RawProbe` | `Minimal`; **Condicional** | Patch específico do ChestCounter; parser reconhece layout conhecido, não é uma decodificação genérica de todas as abilities. |
| `map.safeZones[]`, `battleBusFlightPaths[]`, `llamas[]`, `supplyDrops[]`, `rebootVans[]` | objetos | `MapData` / grupos de rede correspondentes | Conforme grupo; **Condicional** | Estados e horários só quando replicados. |
| Captura de explosões, todos os efeitos de gameplay e eventos globais | — | Nem todos os callbacks/modelos estão habilitados | **Não disponível** em geral | A presença de uma classe no código não comprova que o reader a encaminhe. |

## 14. Outros dados e limitações gerais

| Campo | Tipo | Origem | Evidência/status | Limitações |
|---|---|---|---|---|
| `rawReplayData.parseMode` | string | `ReplayAnalyzer._parseMode` | **Disponível** | Necessário para interpretar por que certas seções estão vazias. |
| `rawReplayData` seção vazia | array/objeto vazio ou null | Resultado construído pelo analisador | **Condicional** | Pode significar ausência no replay ou que o `ParseMode` não leu aquele grupo; consultar `parseMode`. |
| Inventário, POI, movimento ou veículo não observado | — | Sem evidência replicada | **Não disponível** | Não preencher com zero, coordenada calculada ou classificação presumida. |
| Cálculos de distância, duração, médias, visitas a POI e contagens iniciais | — | Responsabilidade posterior da API | **Derivado** | ChestCounter não os calcula no novo snapshot. |
| Idempotência, MatchKey, hash e request de ingestão existente | campos do contrato atual | `ReplayIngestionMapper` | Compatibilidade atual | Permanecem inalterados; `RawReplayData` não faz parte do JSON de ingestão até auditoria do OpenAPI. |

## Cobertura de testes e validação pendente

Os testes cobrem preservação de coordenadas/velocidade/veículo/assento, tags de
POI, tempos nulos até serem observados, deduplicação de atualizações idênticas
do kill feed, aliases de prefixo com ignorar grupos e a exclusão do snapshot
interno do JSON de ingestão atual. O repositório não contém fixtures `.replay`
nos projetos de teste; a validação com replays reais, todos os modos de parsing
e o contrato OpenAPI continua pendente.
