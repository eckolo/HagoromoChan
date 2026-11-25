# アプリケーションアイコン

`HagoromoChan.ico` は、ユーザー指定の既存プラグイン用画像 `hagoromo_plugin_icon.png`（256×256 PNG、9,895 bytes）を変換した共通アイコン。256×256の画像データは元PNGとバイト一致する。16・32・48ピクセル版は同じ画像をLanczosで縮小し、32色に量子化したPNG画像。ICOは4サイズ、12,608 bytes。

- 元PNG SHA-256: `9a50f6d26da55c0f594a5c538edf300f9d3cedc3d270b4befa728f164340152a`
- ICO SHA-256: `11dad41dbc69fc33a6efa67fd23f7c6520d9a29e3ba306e0cd3b75a93da86dd1`
- ICO Git blob SHA-1: `d12a21ec4f7ea3b1559c7b5f3ef63f8c25ae7c69`

LauncherとDesktopの両プロジェクトが `ApplicationIcon` で同じファイルを参照する。ビルド時にWindows exeへ埋め込むので、配置後のショートカット個別設定や外部ICOのコピーは不要。Relayのアイコン、本体のMascot.png、プラグイン側の既存アイコン設定は変更しない。アイコン原本はLauncherの共有入力指紋に含まれるため、変更時には全体ビルドを行う。

Windowsでのexeリソース・表示確認は[M08実機確認手順](../../../docs/operations/M08-verification.md)のW20で行う。
