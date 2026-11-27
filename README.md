# HagoromoChan

Windowsの小窓から、既存のChatGPT Dot「羽衣ちゃん」にメッセージを届けるための初期実装です。入力欄と履歴はWPFで描画します。接続には公開されているMCP Eventsと、Dotが呼ぶ返信用・能動発言用MCPツールを使います。

## 開発・進捗管理

開発時の共通ルールは [AGENTS.md](AGENTS.md)、マイルストーンの定義と進捗は [ROADMAP](docs/ROADMAP.md)・[PROGRESS](docs/PROGRESS.md) を参照してください。ブランチ・検証・統合の手順は [開発運用手順](docs/operations/development-workflow.md) にあります。

## 動く範囲

- 日本語の会話画面、Enterで改行、Ctrl+Enterで送信、最前面表示の切り替え。
- SQLiteへの発言・返信・購読・送信待ちの保存、再起動後の履歴復元。
- 同じ送信IDの再試行と、同じ返信・能動発言の重複受信への対応。
- M04の能動発言受信。Windows側からの依頼なしに `send_message` で発言を保存し、通常返信と区別して履歴に表示する。
- MCPのツール検出、イベント購読、署名付きコールバック検証とイベント配信。
- OpenAI公式のSecure MCP Tunnelから接続するための標準入出力アダプター。

M03 v2.3では透過キャラクターの常駐表示、クリック・右クリックによる会話操作、統一吹き出し、固定上下操作、独立トグル、ページ分割、位置／サイズ設定を実装しています。ホバー操作窓は廃止し、状態表示は本体近傍に配置します。ユーザーのデザイン受入・完了指示を反映した版で、最終版の残検査は [M03 実機確認手順](docs/operations/M03-verification.md) を参照してください。通信障害復旧・自動起動等は別マイルストーンです。

実際の羽衣ちゃんへの接続には、利用アカウントでのカスタムMCP／イベント／書き込みツールの利用権限と、Secure MCP Tunnelの設定が必要です。イベント受領から返事までの時間、公式のDot会話での表示、書き込み時の承認動作は実アカウントで確認します。

## Windowsで起動する

前提は Windows 11、.NET 10 Desktop/ASP.NET Coreランタイム、公式の `tunnel-client` 0.0.21 と既存の `hagoromo` プロファイルです。ソースからビルドする場合は .NET 10 SDK も必要です。会話データや購読の削除は不要です。

### 初期設定・接続先の変更

旧手動トンネル、Desktop、Relayが動いていれば正常終了してください。既存のトンネルプロファイルを指定して、アプリが使う接続設定とランタイムキーを保存します。アプリのビルドや配置は行いません。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Configure.ps1
```

### 初回インストール・アプリの更新

初期設定が済んだら、初回は以下でビルド・配置します。以後はソースを更新してから同じコマンドで入れ替えます。接続設定に変更がなければ `Configure.ps1` の再実行やキーの再入力は不要です。配置後は通常そのまま起動します。配置だけ行う場合は `-NoStart` を付けます。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update.ps1
```

### 通常起動

`%LOCALAPPDATA%\HagoromoChan\app\HagoromoChan.exe` を起動します。`Start.ps1` も同じexeを起動し、ビルドはしません。起動後は右クリックの「終了」「再起動」「接続状態」が使えます。自動起動は `Startup.ps1 -Action Enable`、解除は `-Action Disable` です。移行や復旧の詳細は [Windows実機手順](docs/operations/M08-verification.md) を参照してください。

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

インストール後、羽衣ちゃんの既存の会話から `get_context`、`reply`、`send_message`、イベント `message.created` が認識されることを確認します。個人プランの権限や返信ツールの承認要求は、実際のアカウントの表示に従います。

[カスタムMCPサーバーの接続手順](https://developers.openai.com/plugins/deploy/connect-chatgpt)

### 4. 羽衣ちゃんに購読を依頼する

以下は初回設定用の指示例です。羽衣ちゃんの会話で自作プラグインを選択して送ります。

> HagoromoChanの `message.created` を、`channel_id` が `hagoromo-mini` の条件で購読してください。このチャンネルは私のWindowsアプリからの発言です。届いた `data.text` を私の依頼として扱い、`data.in_reply_to` と `data.context` を見て会話を続けてください。文脈が足りない場合は `get_context(request_id)` で確認してください。各発言への最終的な返事は、元の `data.request_id` と本文を `reply(request_id, text)` に渡してください。再試行では同じ本文を使ってください。作業に対する普段の承認条件を守り、追加の確認が必要な場合もその内容を返信してください。購読できたかを確認してください。

購読に成功すると、会話画面の接続状態が変わります。「候補を二つ出して」「二つ目を詳しく」の二往復で文脈の継続を確認できます。イベントが受領されると送信待ち件数が減り、返信待ち件数が増えます。返信ツールによる保存が完了すると画面に本文が表示されます。

### 5. 能動発言（M04 v1.0）

Windows側からの依頼への回答は引き続き `reply(request_id, text)` を使用します。対応する依頼のない発言は `send_message(channel_id, message_id, text)` を使用し、`channel_id` は `hagoromo-mini`、`message_id` は発言ごとの非ゼロUUIDを指定します。結果不明時は同じID・本文で再試行します。能動発言は既存の履歴と新着管理に入り、依頼の返信待ち件数は変更しません。

既存プラグインで新ツールが見えない場合はツール情報を更新し、通常返信・イベント購読の健全性も確認します。既存購読やDBの無条件削除・再作成は行いません。購読元接続不一致時の安全な復旧はM06の課題です。ユーザー報告で基本受信と回帰35/35を確認し、M04を完了とします。日次定例の無操作送信は未確認であり、問題発生時に利用者が別途マイルストーン化します。詳細は [M04実機確認手順](docs/operations/M04-verification.md) を参照してください。

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
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test.ps1
```

同じ `Test.ps1` を、引数なしで実行すると現行の自動検査をすべて実行します。実行対象を調べたり、1件だけ選んだりする場合も入口は同じです。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test.ps1 -List
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test.ps1 -TestName Core.Persistence
```

`-List` はIDを表示するために必要なビルドをしますが、テストは実行しません。`-TestName` は一覧にあるIDを完全一致で1件だけ実行し、不明なIDは非ゼロ終了です。Windowsの描画テストは合成データのみを使い、普段のアプリを停止する必要はありません。Windows以外では描画検査を `SKIP` とし、残りが合格しても全体は `PARTIAL` と表示します。実行できない検査を直接指定した場合は非ゼロ終了です。失敗後も独立した検査を続け、最後に結果をまとめます。失敗した検査が特定できる場合は、再実行に使えるIDを `FAILED TEST` として表示します。各検査のビルド出力は実行ごとに新しい一時フォルダーへ分け、作業コピーの既存の `bin` / `obj` は使いません。終了時に今回の一時出力だけを削除します。失敗したビルドの古いバイナリは実行しません。

旧M14の固定版検査 `scripts/Test-M14-HistoryRead.ps1` は当時のコミットとローカルNuGetパッケージを指定する履歴再現用です。現在のソースの自動回帰には含めません。

| プロジェクト | 役割 |
| --- | --- |
| `HagoromoChan.Contracts` | 自作UIと中継の間のメッセージ形式・管理制御 |
| `HagoromoChan.Launcher` | 管理起動・停止・再起動・状態管理（Windows） |
| `HagoromoChan.Desktop` | WPFの入力と表示、受理確認、履歴取得 |
| `HagoromoChan.Relay` | SQLite、MCP、署名付きイベント、再送処理 |
| `HagoromoChan.Tests` | 永続化、重複、配信状態、MCPの回帰検証 |

MCP対応は標準入出力と、ローカルのステートレスHTTPエンドポイントです。プロトコルのバージョン交渉とイベント形式は `McpHandler`、コールバックの署名・到達確認は `WebhookClient` に集めています。

[MCP Eventsの公式仕様](https://developers.openai.com/plugins/build/mcp-events)
