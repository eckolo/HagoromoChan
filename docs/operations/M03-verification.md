# M03 実装候補の Windows 確認手順

対象: [M03](../milestones/M03.md) / [画面設計](../design/M03-screen-design.md)

本候補は .NET 10 SDK を必要とする。作業環境に SDK・Windows GUI がないため、以下は実行済みの証拠ではなく、実機検証の手順である。既存会話データを消去しない。トンネル、Relay の設定・購読を変更しない。

## 1. 取得・ビルド

Windows で既存 Desktop を右クリック等で終了する。未コミットの変更がある場合は保護する。PowerShell でリポジトリルートから以下を実行する。

```powershell
git status --short
git fetch origin
git switch --track origin/milestone/M03-character-and-window-ui
dotnet build .\src\HagoromoChan.Desktop --configuration Release
```

既にローカル枝がある場合は `git switch milestone/M03-character-and-window-ui`、`git pull --ff-only` を使う。Relay と tunnel-client は別プロセスのまま稼働させてよい。Desktop のみをビルドするため、稼働中の Relay 出力を再ビルドしない。

起動済みの Relay が正常なら Desktop だけ起動する。

```powershell
Invoke-RestMethod http://127.0.0.1:48732/health
Start-Process .\src\HagoromoChan.Desktop\bin\Release\net10.0-windows\HagoromoChan.Desktop.exe
```

Relay も起動が必要なら通常の起動スクリプトを使用する（稼働中の Relay / stdio プロセスのファイルロックがある場合は、全プロジェクトのビルドに先立ち適切に停止する）。トンネルの初期化・購読の再登録は不要。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start.ps1
```

全プロジェクトのビルドと回帰検証は、必要に応じて稼働中のプロセスのファイルロックを解消したうえで実施する。回帰検証は試験用 DB を使用する。

```powershell
dotnet build .\HagoromoChan.sln --configuration Release
dotnet run --project .\tests\HagoromoChan.Tests --configuration Release
```

## 2. 確認項目

| ID | 確認 | 期待 |
| --- | --- | --- |
| W01 | 起動直後 | 透過の羽衣ちゃんだけが基本表示。過去履歴が新着として大量表示されない。接続障害などがあれば必要な状態パネルのみ出る。 |
| W02 | 立ち絵の透明な周囲、背景上をクリック | 背後のアプリを操作できる。立ち絵クリック時だけ会話を開く。 |
| W03 | マウスオーバー、メニューへの移動、離脱 | 約200msで出現、操作可能、約400msで閉じる。入力フォーカスを奪わない。 |
| W04 | 左クリック、ドラッグ、右クリック | 左クリックで入力、ドラッグで移動（誤送信・誤会話開始なし）、右クリックで設定・終了。 |
| W05 | 会話入力、IME、Enter / Ctrl+Enter | Enter は改行、Ctrl+Enter / 送信ボタンで送信、変換確定で誤送信しない。入力を閉じても下書き保持。 |
| W06 | 別アプリで作業中の実返信 | 吹き出しが新規表示される。他アプリのフォーカスを奪わない。既に古い回答を読んでいる場合は勝手に置換しない。 |
| W07 | 2件以上の返信、前へ／次へ／新着、閉じる | 回答を順次閲覧可能。閉じた回答だけ既読。他の新着は件数・新着操作で残る。長文はスクロール・選択・コピー可能。 |
| W08 | 履歴 | 過去の発言と回答が時系列で残り、読み返し中の新着で位置が勝手に飛ばない。会話入力と保存内容を損なわない。 |
| W09 | 複数モニター、上下左右の端、DPI差、モニター切断 | 本体・各パネルの位置補正。吹き出しの尾の向き、表示・操作可能範囲、背後アプリへのクリックを確認。 |
| W10 | 最前面切替、サイズ切替、位置リセット、再起動 | 設定と位置・未読を復元し、古い回答を二重通知しない。 |
| W11 | 送信失敗・配信失敗 | 既存の送信ID、受付確認、再試行、入力内容保持を維持する。障害再現は安全な試験範囲で行い、稼働中の Dot やトンネルを不用意に止めない。 |
| W12 | 終了 | 本体・パネルが終了する。Relay と tunnel-client の別プロセス運用は変更されない。 |

最初の試験は W01～W06 を優先する。実返信は無害な短文から始め、既存の会話続行（例: 候補を2つ挙げる → 2つ目を詳しく）を確認する。

## 3. 画像素材と表示設定

- 同梱の暫定画像は 123×180 の透過PNG。実機の高DPI・大サイズ表示では粗さが残る。
- 高解像度の透過PNGを `%LOCALAPPDATA%\HagoromoChan\mascot.png` に配置すれば次回起動時に優先する。素材の正式版は今後選定する。
- `desktop-ui.json` に表示位置・サイズ・最前面・通知基準・未読IDを保存する。会話 DB やトークンは変更しない。破損時は元ファイルを保護して復旧する（既読・未読に影響するので安易に消去しない）。
- `HAGOROMO_DATA_DIR` 指定時は既存の同一ディレクトリ設定を維持する。

## 4. 結果の返却

ブランチ名、`git rev-parse HEAD`、ビルドの成否、回帰検査結果、W01～W12の合否／未実施、必要に応じてスクリーンショットを返す。APIキー・購読シークレット・実会話の機密情報は含めない。未確認を合格扱いしない。M03 は完了条件を満たした候補だけを main にマージする。
