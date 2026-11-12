# Validação

Esta alteração não foi compilada nem executada. Não existe projeto de testes
automatizados incluído nesta etapa. A validação deve ser feita em um ambiente
apropriado, preservando cópias de `config.json` e `data.json`.

Verificações manuais prioritárias:

1. Abrir as configurações com dados existentes e com histórico vazio; a prévia e
   os dois painéis devem usar os valores reais, inclusive zero.
2. Ajustar 50%, 70% e 100%; verificar proporções, restauração do padrão,
   persistência após reiniciar e compatibilidade com configuração antiga.
3. Desbloquear e redimensionar o overlay; verificar sincronização da escala na
   interface e no OBS, posição, bloqueio, atalhos e mudanças rápidas concorrentes.
4. Conferir OBS transparente, atualização após mudança de dados e escala, sem
   recarregamento da página, e o arquivo local `overlay.html`.
5. Conferir textos nos dez idiomas, imagens na publicação e ausência de erros
   de carregamento em `errors.log`.
6. Confirmar processamento de partidas, perfis, histórico, relatórios,
   atualização e monitoramento do Fortnite sem regressões.

Além disso, a migração de arquitetura requer testes de contrato e integração
para persistência, replay, relatórios, overlay e composição antes de remover
referências entre projetos. Esses testes e a migração ainda não foram feitos.
