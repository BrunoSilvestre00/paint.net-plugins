# Paint.NET Plugins

Plugins de efeito para o [Paint.NET](https://www.getpaint.net/) 5.x (.NET 11).

## Plugins

| Plugin | Menu | O que faz |
|---|---|---|
| **Erosão de Contorno** | Efeitos → Objeto | Recua o contorno de uma forma N pixels para dentro (erosão morfológica do canal alfa), com borda seca ou com blur. |

### Erosão de Contorno

- **Profundidade da erosão (px)**: 1 a 100.
- **Tipo de borda**: *Seco* (corte nítido) ou *Com blur* (degradê suave).
- **Suavização do contorno**: 0 a 20. Arredonda pontas e dentes de serra que a erosão forma; 0 desliga.

Funciona sobre a transparência da camada (alfa): precisa haver uma forma com fundo transparente. Usa um campo de distância com precisão de subpixel, então bordas com antialias não viram degraus.

## Instalação (usar o plugin)

1. Baixe as DLLs da pasta [`dlls/`](dlls/).
2. Copie para a pasta `Effects` do Paint.NET, normalmente `C:\Program Files\Paint.NET\Effects` (precisa de permissão de administrador).
3. Se o Windows bloquear a DLL, clique com o botão direito → Propriedades → marque **Desbloquear**.
4. Reinicie o Paint.NET.

> Requer Paint.NET 5.2 ou compatível (alvo `net11.0`). Em versões muito diferentes a API pode ter mudado.

## Compilar (desenvolvimento)

Requisitos: [.NET 11 SDK](https://dotnet.microsoft.com/download) e o Paint.NET instalado.

1. Crie um arquivo `.env` na raiz com o caminho do seu Paint.NET:
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
