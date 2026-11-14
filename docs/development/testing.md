# Validação

Execute os testes das regras de domínio com:

```powershell
dotnet test tests\ChestCounter.Core.Tests\ChestCounter.Core.Tests.csproj -c Release
```

Valide o catálogo localizado e compile a aplicação com:

```powershell
.\tools\Validate-Localization.ps1
dotnet build src\ChestCounter\ChestCounter.csproj -c Release
```

No laboratório de replay, os JSONs dos modos de parsing representam valores de
ponto flutuante não finitos como as strings `"NaN"`, `"Infinity"` e
`"-Infinity"`, pois JSON não possui números equivalentes. A aba Logs indica a
etapa da falha e conserva a exceção original; leitura, decodificação, parsing e
construção do resultado compartilham a chamada `ReadReplay` da biblioteca.

Verificações manuais prioritárias:

1. Confirmar escopo de recompensa própria/equipe/outro no relatório e alternar a
   opção de incluir tesouros da equipe sem alterar `data.json`.
2. Confirmar alerta do token apenas quando houver identificador ou nome
   semanticamente explícito; recompensas desconhecidas não devem acionar alerta.
3. Confirmar que os cartazes Se Busca não entram na contagem de baús, mas os
   cofres associados continuam classificados como cofres de cartaz.
4. Confirmar que playlists criativas ou desconhecidas não entram nas
   estatísticas Battle Royale e que as playlists BR suportadas permanecem.
5. Conferir textos nos dez idiomas e ausência de erros de carregamento em
   `errors.log`.
