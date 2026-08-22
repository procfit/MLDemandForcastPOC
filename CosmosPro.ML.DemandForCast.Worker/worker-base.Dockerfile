# Imagem base do `worker` — o único serviço deste repositório que treina modelo.
#
# Existe por causa de uma dependência nativa: o nativo do LightGBM
# (`runtimes/linux-x64/native/lib_lightgbm.so`, do pacote `LightGBM` que vem por
# `Microsoft.ML.LightGbm`) declara `libgomp.so.1` em DT_NEEDED, e nenhuma imagem oficial do
# .NET traz esse pacote — conferido também no `-chiseled-extra`, cujo "extra" é ICU e tzdata.
# Sem ele, o primeiro `dlopen` do treino falha com "Unable to load shared library
# 'lib_lightgbm'" e a sessão de comparação termina em `Falha` pedindo ao comprador que
# reenvie o ZIP, que nunca resolve, porque o problema não é o ZIP.
#
# Não dá para instalar o pacote na imagem do app: o container publishing do SDK monta camadas
# sobre uma base, não executa `RUN`. Daí esta base, construída e empurrada pelo CI **antes**
# do `aspire do push` (ver `.github/workflows/ci-imagens.yml`) e referenciada por
# `ContainerBaseImage` no `.csproj` do worker.
#
# `aspnet`, e NÃO `runtime`, e isso custou dois dias de produção. O `worker` é um
# `Microsoft.NET.Sdk.Worker`, mas referencia `ServiceDefaults`, que declara
# `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (health checks e instrumentação
# OTel de ASP.NET Core). A imagem `dotnet/runtime` traz só `Microsoft.NETCore.App`, então o
# processo morre em ~2 segundos, sempre, com "You must install or update .NET to run this
# application / No frameworks were found" e exit code 150 — imagem que sobe, container que
# some, fila que para. `ContainerBaseImage` **sobrescreve** a escolha que o SDK faria; ao
# fixá-la à mão, a base precisa carregar todos os frameworks que o app referencia.
#
# Só o worker precisa desta imagem: `Forecasting` entra por `Purchasing`, e os dois só são
# referenciados por ele — `apiservice` e `webfrontend` seguem na base default do SDK.
#
# A tag desta base **não** é imutável, de propósito: o CI reconstrói a cada execução, então
# correção de segurança do sistema entra sozinha. É seguro porque a base é insumo de build —
# a imagem do worker carrega as camadas dela, não uma referência a ela.
#
# Windows não precisa de nada disso, e é por isso que o `F5` nunca mostrou nem o problema do
# LightGBM: lá o nativo é `lib_lightgbm.dll`, contra o OpenMP da MSVC, que já existe na máquina.
FROM mcr.microsoft.com/dotnet/aspnet:10.0

RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 \
    && rm -rf /var/lib/apt/lists/*

# As duas asserções que descrevem, juntas, tudo que esta base precisa entregar. Falham o build
# da base em vez de deixar o defeito viajar até o destino: a primeira pega um rename do pacote
# do OpenMP, a segunda pega uma troca de `aspnet` por `runtime` (a que derrubou a produção).
RUN ldconfig -p | grep -q 'libgomp\.so\.1' \
    && dotnet --list-runtimes | grep -q '^Microsoft.AspNetCore.App 10\.' \
    && dotnet --list-runtimes | grep -q '^Microsoft.NETCore.App 10\.'
