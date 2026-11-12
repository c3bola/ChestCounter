# Overlay interno e OBS

`OverlayModel.Build` produz `OverlayState` a partir do perfil e das partidas
reais. `Monitor` publica um quadro inicial e novos estados. `OverlayVisual` é
um `Canvas` Avalonia com dois painéis e imagens locais; não existe WebView na
janela interna. O host renderiza a composição fora da tela, produzindo PNG e
pixels para a janela transparente Win32. O mesmo PNG é distribuído pelo
`ObsServer` na interface de loopback, sem servidor adicional.

O OBS usa uma Browser Source com a URL informada nas configurações. A página
consulta `/view` a cada dois segundos e busca `/image` quando a revisão muda,
sem recarregar a página. O arquivo local `overlay.html` usa o mesmo PNG e
atualiza a página a cada dez segundos. Sem dados, o estado construído pelo modelo
contém totais zero; a estrutura visual não depende da existência de partidas.

`Config.OverlayScalePercent` é persistido no JSON: padrão 70%, limitado a
50–100%. As configurações oferecem ajuste em passos de 10%, prévia da mesma
composição e restauração do padrão. Ao redimensionar a janela desbloqueada, o
valor é arredondado para um desses passos e persistido. O OBS recebe o mesmo
percentual. Na Browser Source ele é relativo à área configurada no OBS; portanto
o tamanho em pixels não é necessariamente igual ao da janela interna. Ajuste
a área da fonte proporcionalmente se necessário.

O desenho usa `chest-dashboard.png`, `logo.png`, `fireworks.png`,
`extractor.png`, `lucky_Locator.png` e a fonte `burbarkfont.ttf`, empacotados
como recursos Avalonia. As imagens são carregadas individualmente e erros são
registrados; uma imagem ausente não impede os demais painéis de serem desenhados.
O host descarta os bitmaps ao encerrar; a prévia os descarta ao fechar a janela.
