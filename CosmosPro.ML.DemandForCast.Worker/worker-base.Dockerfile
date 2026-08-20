# Imagem base do `worker` — o único serviço deste repositório que treina modelo.
#
# `mcr.microsoft.com/dotnet/runtime:10.0`, que é o default do container publishing do SDK,
# traz `libstdc++.so.6` mas **não** traz `libgomp1`. O nativo do LightGBM
# (`runtimes/linux-x64/native/lib_lightgbm.so`, do pacote `LightGBM` 3.3.5 que vem por
# `Microsoft.ML.LightGbm`) declara `libgomp.so.1` em DT_NEEDED, então o primeiro `dlopen`
# do treino falha com "Unable to load shared library 'lib_lightgbm'" e a sessão de
# comparação termina em `Falha` — pedindo ao comprador que reenvie o ZIP, que nunca
# resolve, porque o problema não é o ZIP.
#
# Não dá para instalar o pacote na imagem do app: o container publishing do SDK monta
# camadas sobre uma base, não executa `RUN`. Daí esta base, construída e empurrada pelo CI
# **antes** do `aspire do push` (ver `.github/workflows/ci-imagens.yml`) e referenciada por
# `ContainerBaseImage` no `.csproj` do worker.
#
# Só o worker precisa dela: `Forecasting` entra por `Purchasing`, e os dois só são
# referenciados pelo worker — `apiservice` e `webfrontend` seguem na base do SDK.
#
# A tag desta base **não** é imutável, de propósito: o CI reconstrói a cada execução, então
# correção de segurança do Debian entra sozinha. É seguro porque a base é insumo de build —
# a imagem do worker carrega as camadas dela, não uma referência a ela.
#
# Windows não precisa de nada disso, e é por isso que o `F5` nunca mostrou o problema: lá o
# nativo é `lib_lightgbm.dll`, contra o OpenMP da MSVC, que já existe na máquina.
FROM mcr.microsoft.com/dotnet/runtime:10.0

RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 \
    && rm -rf /var/lib/apt/lists/*

# Falha o build da base se o pacote deixar de entregar a biblioteca com este nome — sem
# isso, um rename upstream volta a ser descoberto em produção, no meio de um treino.
RUN ldconfig -p | grep -q 'libgomp\.so\.1'
