# Paint.NET Plugins

Plugins de efeito para o [Paint.NET](https://www.getpaint.net/) 5.x (.NET 11).

## Plugins

| Plugin | Nome em inglês | Menu | O que faz |
|---|---|---|---|
| **Erosão de Contorno** | **Contour Erosion** | Efeitos → Objeto (*Effects → Object*) | Recua o contorno de uma forma N pixels para dentro (erosão morfológica do canal alfa), com borda seca ou com blur. |
| **Inclinação** | **Skew** | Efeitos → Distorção (*Effects → Distort*) | Inclina (cisalhamento/skew) o conteúdo da seleção com uma roleta de ângulo, na horizontal ou na vertical. |
| **Rotação** | **Rotate** | Efeitos → Distorção (*Effects → Distort*) | Rotaciona o conteúdo da seleção com uma roleta de ângulo, em torno do centro do objeto ou da seleção. |
| **ShapeFit** | **ShapeFit** | Efeitos → Distorção (*Effects → Distort*) | "Liquify automático": deforma o objeto da camada para preencher a forma da seleção. Sem janela, aplica direto. |

> The plugins are named in Portuguese inside Paint.NET; the English names above are for reference (and match the DLL names: `ContourErosion.dll`, `Skew.dll`, `Rotate.dll`, `ShapeFit.dll`).

### Erosão de Contorno (Contour Erosion)

- **Profundidade da erosão (px)**: 1 a 100.
- **Tipo de borda**: *Seco* (corte nítido) ou *Com blur* (degradê suave).
- **Suavização do contorno**: 0 a 20. Arredonda pontas e dentes de serra que a erosão forma; 0 desliga.

Funciona sobre a transparência da camada (alfa): precisa haver uma forma com fundo transparente. Usa um campo de distância com precisão de subpixel, então bordas com antialias não viram degraus.

### Inclinação (Skew)

- **Ângulo de inclinação**: roleta de -180° a 180°, dobrada para a faixa útil de -85° a 85°.
- **Eixo**: *Horizontal* (bordas verticais inclinam) ou *Vertical* (bordas horizontais inclinam).
- A inclinação é em torno do centro da seleção, com interpolação bilinear em alfa pré-multiplicado.
- O Paint.NET só deixa um efeito escrever dentro da seleção: o que sairia dela é cortado. Selecione uma área maior que a imagem para ter folga.

### Rotação (Rotate)

- **Ângulo de rotação**: roleta de -180° a 180°. Positivo gira no sentido anti-horário.
- **Pivô**: *Centro do objeto* (caixa dos pixels visíveis, bom para sprites com margem transparente) ou *Centro da seleção*.
- Mesma limitação da Inclinação: o que sair da seleção é cortado, então selecione uma área maior que a imagem.

### ShapeFit

Deforma o objeto da camada atual (os pixels visíveis) até a silhueta dele coincidir com a forma da seleção. Não tem janela de opções: é aplicado direto (Ctrl+Z desfaz).

**Fluxo recomendado (não destrutivo):**

1. Com o objeto numa camada, crie uma **camada nova vazia acima dela** e deixe-a selecionada.
2. Faça uma seleção com a forma de destino (sem seleção, o alvo é a camada inteira).
3. Efeitos → Distorção → ShapeFit.

Como a camada atual está vazia, o ShapeFit pega o objeto da **camada mais próxima abaixo** que tenha conteúdo e escreve o resultado na camada nova. O original fica intacto na camada de baixo: esconda ou apague essa camada se não quiser vê-lo.

**Se o objeto estiver na própria camada atual**, ele é usado de lá, mas o que estiver fora da seleção fica como estava: um efeito do Paint.NET só escreve dentro da seleção. Para ficar só com o resultado, depois de aplicar use `Ctrl+I` (inverter seleção) e `Delete`.

Como funciona: alinha o objeto à seleção pelos momentos (centro, escala e eixos) e depois registra as duas silhuetas com um warp não-rígido de baixa distorção, feito em campos de distância com sinal, da escala grossa para a fina, com relaxamento ARAP (penaliza esticar e cisalhar, não girar). O código do algoritmo é puro (`src/ShapeFit/ShapeRegistration.cs`) e tem testes em `src/ShapeFit.Tests`.

## Instalação (usar o plugin)

1. Baixe o `PaintNET-Plugins.zip` da [última release](../../releases/latest).
2. Extraia o zip. Ele traz uma pasta `Effects` com todas as DLLs.
3. Copie o conteúdo de `Effects` para a pasta `Effects` do Paint.NET, normalmente `C:\Program Files\Paint.NET\Effects` (precisa de permissão de administrador).
4. Se o Windows bloquear as DLLs, clique com o botão direito em cada uma → Propriedades → marque **Desbloquear**.
5. Reinicie o Paint.NET.

A release é gerada automaticamente a partir da pasta [`dlls/`](dlls/) a cada alteração na `main`.

> Requer Paint.NET 5.2 ou compatível (alvo `net11.0`). Em versões muito diferentes a API pode ter mudado.

## Compilar (desenvolvimento)

Requisitos: [.NET 11 SDK](https://dotnet.microsoft.com/download) e o Paint.NET instalado.

1. Copie `.env.example` para `.env` e ajuste o caminho do seu Paint.NET:
   ```
   PAINT_NET_PATH="C:\Program Files\Paint.NET"
   ```
2. Rode:
   ```powershell
   .\deploy.ps1                       # compila tudo
   .\deploy.ps1 -Project ContourErosion
   ```
   O script compila, copia a DLL para `dlls\` e instala em `$PAINT_NET_PATH\Effects` (pede elevação/UAC se preciso).

Novos plugins ficam em `src\<Nome>\<Nome>.csproj`; as referências à API do Paint.NET vêm de `Directory.Build.props`.
