# HagoromoChan

Windowsの小窓から、既存のChatGPT Dot「羽衣ちゃん」にメッセージを届けるための初期実装です。入力欄と履歴はWPFで描画します。接続には公開されているMCP Eventsと、Dotが呼ぶ返信用MCPツールを使います。

## 開発・進捗管理

開発時の共通ルールは [AGENTS.md](AGENTS.md)、マイルストーンの定義と進捗は [ROADMAP](docs/ROADMAP.md)・[PROGRESS](docs/PROGRESS.md) を参照してください。ブランチ・検証・統合の手順は [開発運用手順](docs/operations/development-workflow.md) にあります。

## 動く範囲

- 日本語の会話画面、Enterで改行、Ctrl+Enterで送信、最前面表示の切り替え。
- SQLiteへの発言・返信・購読・送信待ちの保存、再起動後の履歴復元。
- 同じ送信IDの再試行と、同じ返信の重複受信への対応。
- MCPのツール検出、イベント購読、署名付きコールバック検証とイベント配信。
- OpenAI公式のSecure MCP Tunnelから接続するための標準入出力アダプター。

M03 v2.3では透過キャラクターの常駐表示、クリック・右クリックによる会話操作、統一吹き出し、固定上下操作、独立トグル、ページ分割、位置／サイズ設定を実装しています。ホバー操作窓は廃止し、状態表示は本体近傍に配置します。ユーザーのデザイン受入・完了指示を反映した版で、最終版の残検査は [M03 実機確認手順](docs/operations/M03-verification.md) を参照してください。通信障害復旧・自動起動等は別マイルストーンです。

実際の羽衣ちゃんへの接続には、利用アカウントでのカスタムMCP／イベント／書き込みツールの利用権限と、Secure MCP Tunnelの設定が必要です。イベント受領から返事までの時間、公式のDot会話での表示、書き込み時の承認動作は実アカウントで確認します。

## Windowsで起動する（M08 v1.0.4）

Windows 11、.NET 10 SDK（開発用）、.NET 10 Desktop/ASP.NET Coreランタイム、既存の公式tunnel-client 0.0.21とhagoromoプロファイルを使用します。初回は旧手動トンネル、Desktop、Relayを正常終了してください。会話データや購読の削除は不要です。

初回の設定・ビルド配置（既存プロファイルとruntimeキーを使用）:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Configure.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update.ps1

通常起動は %LOCALAPPDATA%\HagoromoChan\app\HagoromoChan.exe の起動だけです。Start.ps1 は同じexeを起動し、ビルドしません。右クリック「終了」「再起動」は管理下の全プロセスを対象とし、「接続状態」は既存状態窓を拡張します。スタートアップは Startup.ps1 -Action Enable で明示登録し、Disable で解除します。ソース更新はgit pull --ff-only等で行った後に Update.ps1 を実行します。開発用の個別再起動や確認は Control.ps1 を使用します。

詳しい移行・更新・テスト手順は [M08実機確認手順](docs/operations/M08-verification.md) を参照してください。M08 v1.0.4はユーザーの全般的な実機動作成功報告と受入に基づき完了とし、個別未検査は実機確認手順に残します。初回以降の更新時にキーを再入力する必要はありません。

個別開発起動では、従来どおり先にRelay、別ターミナルでDesktopを実行できます（管理起動と同時には実行しない）。

    dotnet run --project .\src\HagoromoChan.Relay
    dotnet run --project .\src\HagoromoChan.Desktop

## 羽衣ちゃんを接続する

### 1. ローカル中継を起動する

管理起動ではLauncherがRelayを起動します。以下は初回のプロファイル準備や個別開発時の手順で、管理起動中には手動のトンネルを重複起動しません。Relayは `127.0.0.1:48732` で待ち受けます。以下で最小の稼働確認ができます。

```powershell
Invoke-RestMethod http://127.0.0.1:48732/health
```

### 2. 公式トンネルを用意する

Platformのトンネル設定で、自分のPlatform組織と、羽衣ちゃんがいるChatGPTワークスペースに関連付けたトンネルを作成します。公式 `tunnel-client` をインストールし、PATHを通してください。必要な権限と配布先はOpenAIの手順を参照してください。

[Secure MCP Tunnelの公式手順](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels)

リポジトリのルートから実行する設定例です。`YOUR_TUNNEL_ID` は作成した値に置き換えます。APIキーはその場で入力し、このPowerShellプロセスの環境変数に保持します。

```powershell
$env:CONTROL_PLANE_API_KEY = [System.Net.NetworkCredential]::new('', (Read-Host 'Tunnel runtime API key' -AsSecureString)).Password
$relayDll = (Resolve-Path '.\src\HagoromoChan.Relay\bin\Release\net10.0\HagoromoChan.Relay.dll').Path
$dotnetPath = (Get-Command dotnet).Source
$mcpCommand = '"' + $dotnetPath + '" "' + $relayDll + '" --mcp-stdio'

tunnel-client init --sample sample_mcp_stdio_local --profile hagoromo --tunnel-id YOUR_TUNNEL_ID --mcp-command $mcpCommand
tunnel-client doctor --profile hagoromo --explain
tunnel-client run --profile hagoromo --mcp.stdio-send-initialized-notification=true
```

トンネルは `--mcp-stdio` のプロセスを起動します。このプロセスは、起動済みのローカルRelayへMCP要求を中継します。会話画面からの送信、購読情報、返事はすべて同じRelayで扱われます。

### 3. ChatGPTのプラグインとして接続する

ChatGPTのプラグイン画面でカスタムMCPサーバーを追加し、接続方法にTunnel、上記のトンネルを指定します。この構成のMCP認証はトンネルのアクセス権で管理するため、プラグイン側では追加のOAuth認証を設定しません。ローカルHTTPには別途自動生成のBearerキーが使われます。

インストール後、羽衣ちゃんの既存の会話から `get_context` と `reply`、イベント `message.created` が認識されることを確認します。個人プランの権限や返信ツールの承認要求は、実際のアカウントの表示に従います。

[カスタムMCPサーバーの接続手順](https://developers.openai.com/plugins/deploy/connect-chatgpt)

### 4. 羽衣ちゃんに購読を依頼する

以下は初回設定用の指示例です。羽衣ちゃんの会話で自作プラグインを選択して送ります。

> HagoromoChanの `message.created` を、`channel_id` が `hagoromo-mini` の条件で購読してください。このチャンネルは私のWindowsアプリからの発言です。届いた `data.text` を私の依頼として扱い、`data.in_reply_to` と `data.context` を見て会話を続けてください。文脈が足りない場合は `get_context(request_id)` で確認してください。各発言への最終的な返事は、元の `data.request_id` と本文を `reply(request_id, text)` に渡してください。再試行では同じ本文を使ってください。作業に対する普段の承認条件を守り、追加の確認が必要な場合もその内容を返信してください。購読できたかを確認してください。

購読に成功すると、会話画面の接続状態が変わります。「候補を二つ出して」「二つ目を詳しく」の二往復で文脈の継続を確認できます。イベントが受領されると送信待ち件数が減り、返信待ち件数が増えます。返信ツールによる保存が完了すると画面に本文が表示されます。

## 保存と障害時の動作

保存先は `%LOCALAPPDATA%\HagoromoChan` です。RelayとDesktopを同じWindowsユーザーで起動してください。

| ファイル | 内容 |
| --- | --- |
| `conversations.db` と付随するSQLiteファイル | 会話、購読、署名情報、イベントの配信状態 |
| `relay.token` | ローカル接続キー。初回起動で自動生成 |
| `pending-message.json` | 受付結果が未確認の発言。送信前に保存し、受理を確認してから消去 |
| `relay.lock`、`desktop.lock` | 同じ保存先を使うプロセスの重複起動を防ぐためのロック |
| `launcher.lock`、`deployment.lock` | 管理起動・更新の競合防止 |
| `launcher.json` | トンネルの設定と同一WindowsユーザーのDPAPIで暗号化したruntimeキー（共有・Git登録しない） |
| `app/`、`logs/` | 配置済みアプリと管理ログ。既存DB・購読と分離 |

検証用に保存先を変更する場合は、Relay、Desktop、トンネルの起動元すべてで `HAGOROMO_DATA_DIR` に同じ絶対パスを指定します。データやキーはリポジトリに含めません。

一時的な配信失敗には同じイベントIDで最大8回まで間隔を空けて再試行します。ChatGPTが受領済みの依頼は、返信待ちが長くなっても再送しません。受付結果が不明なローカル送信は画面の再試行ボタンで同じIDを使い、重複を防ぎます。イベント配信の再試行が止まった場合は、接続を修復してから画面の配信再試行を使います。購読の失効や通知先の無効化には羽衣ちゃん側での購読の再確認が必要です。

購読解除時にすでに通信を開始したイベントは、最後の1件が届く場合があります。解除後の新しい配信は停止します。

PCのスリープ中はローカル中継へのツール呼び出しを受け取れません。稼働中に受け取り済みの返事は保存されます。PCが停止している間の回答配送は実アカウントでの検証対象です。

## 開発と検証

```powershell
dotnet build .\HagoromoChan.sln --configuration Release
dotnet run --project .\tests\HagoromoChan.Tests --configuration Release
```

| プロジェクト | 役割 |
| --- | --- |
| `HagoromoChan.Contracts` | 自作UIと中継の間のメッセージ形式・管理制御 |
| `HagoromoChan.Launcher` | 管理起動・停止・再起動・状態管理（Windows） |
| `HagoromoChan.Desktop` | WPFの入力と表示、受理確認、履歴取得 |
| `HagoromoChan.Relay` | SQLite、MCP、署名付きイベント、再送処理 |
| `HagoromoChan.Tests` | 永続化、重複、配信状態、MCPの回帰検証 |

MCP対応は標準入出力と、ローカルのステートレスHTTPエンドポイントです。プロトコルのバージョン交渉とイベント形式は `McpHandler`、コールバックの署名・到達確認は `WebhookClient` に集めています。

[MCP Eventsの公式仕様](https://developers.openai.com/plugins/build/mcp-events)
