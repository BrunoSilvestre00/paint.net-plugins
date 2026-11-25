# Paint.NET Plugins

Plugins de efeito para o [Paint.NET](https://www.getpaint.net/) 5.x (.NET 11).

## Plugins

| Plugin | Menu | O que faz |
|---|---|---|
| **Erosão de Contorno** | Efeitos → Objeto | Recua o contorno de uma forma N pixels para dentro (erosão morfológica do canal alfa), com borda seca ou com blur. |

| **Inclinação** | Efeitos → Distorcer | Inclina (cisalhamento/skew) o conteúdo da seleção com uma roleta de ângulo, na horizontal ou na vertical. |

### Erosão de Contorno

- **Profundidade da erosão (px)**: 1 a 100.
- **Tipo de borda**: *Seco* (corte nítido) ou *Com blur* (degradê suave).
- **Suavização do contorno**: 0 a 20. Arredonda pontas e dentes de serra que a erosão forma; 0 desliga.

Funciona sobre a transparência da camada (alfa): precisa haver uma forma com fundo transparente. Usa um campo de distância com precisão de subpixel, então bordas com antialias não viram degraus.

### Inclinação

- **Ângulo de inclinação**: roleta de -180° a 180°, dobrada para a faixa útil de -85° a 85°.
- **Eixo**: *Horizontal* (bordas verticais inclinam) ou *Vertical* (bordas horizontais inclinam).
- A inclinação é em torno do centro da seleção, com interpolação bilinear em alfa pré-multiplicado.
- O Paint.NET só deixa um efeito escrever dentro da seleção: o que sairia dela é cortado. Selecione uma área maior que a imagem para ter folga.

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
