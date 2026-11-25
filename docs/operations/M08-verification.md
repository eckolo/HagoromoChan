# M08 v1.0 Windows実機確認手順

対象: [M08](../milestones/M08.md) / [設計](../design/M08-runtime-management.md)。候補版の検査であり成功記録ではない。

## 1. 初回移行

現在の手動トンネルを起動したコンソールでCtrl+C、stdio子プロセスの終了を確認してからDesktop・Relayを正常終了する。RelayのコンソールはCtrl+C。残存があればPID・実行コマンドラインを照合する。無関係なdotnet.exeを一括終了しない。DB、購読、トークン、プロファイルを削除しない。通常データディレクトリを使用する。

リポジトリルートで、ローカルの未コミット変更を保護したうえで実行:

    git status --short
    git fetch origin
    git switch --track origin/milestone/M08-startup

既にローカル枝があれば git switch milestone/M08-startup と git pull --ff-only を利用する。

初回のみ、Windows PowerShellで既存のtunnel-client 0.0.21とhagoromoプロファイルを設定。runtimeキー（adminキーではない）を対話式に入力する。値はログ・チャットに貼らない。

    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Configure.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update.ps1

Configure.ps1は公式クライアントの `profiles list --json` で既存hagoromoのパスを取得する。WindowsのAPPDATA・HOME・XDG・プロファイルディレクトリ上書きはクライアント自身の解決規則に従う。`tunnel-client profiles list` で確認可能。別ディレクトリ指定で運用している場合等は -ProfileFile に既存ファイルの絶対パスを渡す。見つからない場合は新規作成せず設定処理を停止する。PATHにtunnel-clientがなければ -TunnelExecutable を明示する。既に設定したキーを維持してログレベルや実行ファイルの指定を変更する場合は、管理アプリ終了後に Configure.ps1 -ReuseKey を実行する。

Configure.ps1が設定保存に成功した後は、通常起動・ソース更新・ビルド再試行でキーの再入力は不要。設定変更時は必要に応じて -ReuseKey を使用する。DPAPI復号・トンネル認証は起動後に確認する。

Update.ps1 は新規候補をビルドしてから配置し起動する。旧手動起動が残る場合は配置を拒否する。旧出力の上書き・強制停止は行わない。ビルド失敗時はエラーを返し、既存配置を維持する。

## 2. 日常操作・更新

- エクスプローラーで %LOCALAPPDATA%\HagoromoChan\app\HagoromoChan.exe を起動するだけで通常起動。Start.ps1 も同じexeを起動し、ビルドしない。
- 右クリック「再起動」「終了」は管理アプリ全体の再起動・終了。「接続状態」は既存窓を展開・縮小。
- PowerShellから開発用に Control.ps1 -Action Status / Stop / Restart / RestartDesktop / RestartTunnel を使用できる。Restart系の応答は受付であり終了・起動の完了ではない。Statusで確認する。
- ソース更新後は Update.ps1。Desktopのみを変更した場合は Update.ps1 -Target Desktop（共有入力変更時は全体ビルド）。配置切替時は全体を停止する。Build.ps1だけなら候補を作るのみ。Update.ps1 -StageDirectory に候補のパスを渡してビルドなしで配置可能。
- 古いapp-previous-*は退避用。削除は旧配置が不要と確認した後にユーザーが行う。DB・設定のある親フォルダーを削除しない。

スタートアップ登録は通常操作が成功した後に明示実行する（今回は未登録）:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Startup.ps1 -Action Enable
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Startup.ps1 -Action Status

解除は -Action Disable。Windowsの「スタートアップ アプリ」で登録・有効状態も確認する。GUI右クリックメニューに登録項目はない。ログオン検査は利用者の都合のよい時点で実施する。

## 3. 検証項目

| ID | 操作 | 期待 |
| --- | --- | --- |
| M08-W01 | 初回配置・通常起動 | Launcher、Relay、Tunnel、stdio、Desktopの正しい関係・PID、ビルドなしの通常起動、Relay準備後にTunnel/GUI開始。 |
| M08-W02 | exeを二重起動 | 既存アプリを維持。DBロック、ポート、トンネル重複なし。 |
| M08-W03 | GUIの終了 | Desktop→Tunnel/stdio→Relay→Launcherの順で停止。logs/launcher.logで正常停止／Job強制終了の区別。タスクマネージャで対象PID消失、ロック解放、再起動可能。 |
| M08-W04 | GUI再起動・複数回反復 | 旧PIDが終了した後で新しい子PIDが開始。同じ管理プロセス、DB・購読・設定を維持。 |
| M08-W05 | Control.ps1 -Action RestartDesktop | Desktop PIDのみ変更。Relay・Tunnelは同じPID。ビルドなし。 |
| M08-W06 | Control.ps1 -Action RestartTunnel | 旧Tunnelとstdioが消えた後に新規Tunnel起動。Relay・Desktop維持。初期化通知補完設定が有効。 |
| M08-W07 | 接続状態トグル | 健康状態でも右クリックで詳細表示。⌄/⌃で同じ状態窓を展開・縮小。PID、Relay/Tunnel状態、ローカル購読とクラウド未確認の区別。 |
| M08-W08 | ソース更新・ビルド | 稼働中にBuild.ps1、旧DLLロックに阻害されない。Update.ps1で安全に切替。失敗時は旧版とデータ維持。Desktop-onlyの共有入力チェックも確認。 |
| M08-W09 | 意図的な異常終了（隔離データでのみ） | 管理プロセス強制終了後、Jobに属する子・stdioが残らない。別アプリは停止されない。 |
| M08-W10 | 通信異常・未設定・子終了（隔離データ） | トンネル未準備・終了を表示、Desktop操作可能。Relay異常時にトンネルを停止。別のポート／旧プロセスを取り込まない。 |
| M08-W11 | 既存データと会話往復 | 履歴・未読・購読・受付不明のIDを維持。購読を無条件再登録しない。既存Dotに無害なテスト発言、返信受領。 |
| M08-W12 | スタートアップEnable・ログオン・Disable | 通常起動と同じexeで全体起動。無効時は起動なし。二重起動なし。登録先は固定。 |
| M08-W13 | 資格情報・ログ | DPAPIの同一Windowsユーザー復号、別ユーザーでの失敗は隔離検証。秘密がGit・起動引数・ログにないこと。確認時に秘密そのものを共有しない。 |
| M08-W14 | 停止タイムアウト（隔離データ） | 管理下Jobのみのフォールバック。停止できない場合は更新を停止し原因を表示。 |
| M08-W15 | 空白・日本語パス | プロファイル・tunnel-client・データディレクトリのパスを含む起動。初期化、stdioツール、購読を確認。 |
| M08-W16 | 既存プロファイル探索 | HOME環境変数なしのWindows標準配置、公式のディレクトリ上書き、明示 -ProfileFile の場合を確認。既存hagoromoを使用し、未発見時はキー入力・設定保存前に停止。 |
| M08-W17 | ビルドログと再試行 | 日本語Windows PowerShellで全publishの結果が読めること、失敗時に設定の再入力が不要であること、出力エンコーディングが復元されることを確認。 |
| M08-W18 | トンネルのstdioコマンド | Windowsの絶対パスのバックスラッシュが公式クライアントの解析後も維持される。空白・日本語を含むパスも確認。Tunnel/stdio起動、readyz、実際のMCP往復を確認。 |
| M08-W19 | 状態枠の本体幅追従 | 小・中・大、左右端、移動、通常・詳細の切替で本体と同じ実表示幅。上下配置で左右端を揃える。会話表示中は吹き出しを覆わず、詳細本文や操作の可読性も確認。近傍に収まらない場合は右クリックの代替操作を確認。 |
| M08-W20 | アプリケーションアイコン | Update.ps1の全体ビルド・配置後、HagoromoChan.exeおよびDesktop/HagoromoChan.Desktop.exeに同じ既存プラグイン画像由来のアイコンが埋め込まれる。エクスプローラーの大小表示と新規ショートカットで確認する。以前のアイコンが残る場合はキャッシュとの切り分けを行う。 |

正常終了の検査ではPIDだけでなくJob内のstdio子プロセスも照合する。ログは logs/launcher.log、relay.log、desktop.log、tunnel.log（標準出力・エラー）、tunnel-client.log（公式クライアントログ）。状態の観測時刻を確認する。実データで故意に強制終了・障害注入を行わない。

## 4. ビルド・回帰検査

    dotnet build .\HagoromoChan.sln --configuration Release
    dotnet run --project .\tests\HagoromoChan.Tests --configuration Release

既存26検査に現在セッションの状態、ローカルパイプ、Windows引数引用の3検査を追加。v1.0.3でトンネル固有の引数引用と本体幅・非重複配置の2検査を追加した（計31、未実行）。PowerShellスクリプトの構文・動作、WindowsのJob/DPAPI、実際のMCP、GUI・ログオンも別途確認する。結果、HEAD SHA、失敗時のエラーと機密を除いたログを返却する。M08の完了判断と未取得の個別検査は末尾の受入記録および個別マイルストーン記録を参照する。

2026-11-25 実機報告: v1.0.1のConfigure.ps1は既存プロファイルを検出し設定保存成功。Launcher/Relay publish成功、DesktopはCS0104で失敗し配置前に停止。v1.0.2に更新後、Configure.ps1は繰り返さずUpdate.ps1を再実行する。文字化け対策・修正版ビルド・起動は未確認。

2026-11-25 追記: v1.0.2の管理プロセス・Relay・Desktop起動を確認したが、Tunnelはstdio実行パスのバックスラッシュ脱落で異常終了した。v1.0.3ではトンネル固有のパーサ向け引用を修正し、状態枠の幅をユーザー指示に従って本体と同じにした。作業枝の最新版を取得した後、保存済み設定を維持して通常のUpdate.ps1を実行する。実際の起動・通信・配置は再検証待ち。会話・購読・資格情報は変更しない。Gitの取得・枝選択手順は本手順書に維持するが、ユーザーへの日常の再試行案内では省略する。

2026-11-25 追記: v1.0.3の正常起動・会話往復成功をユーザーから受領。v1.0.4では[共通ICO](../../src/HagoromoChan.Launcher/Assets/README.md)を追加し、LauncherとDesktopのApplicationIconに指定した。通常のUpdate.ps1（全体ビルド）で配置する。既存のスタートアップショートカットは固定exeを参照するため作り直しは原則不要。実際のアイコンリソース・エクスプローラー表示は未検証。Windowsのアイコンキャッシュで古い表示が残る場合は、新規ショートカットや別の場所にコピーしたexeで比較する（元の配置と設定は変更しない）。

## 5. v1.0.4受入・残検査

2026-11-25、固定版 `bb6dc09fa8bef12fba58b257314ad20f556a745f` に対して、ユーザーから「一通りうまく動いた」との報告とM08の完了指示を受領した。これを最終版の全般的な受入根拠とする。v1.0.3の管理起動・会話往復は明示的な成功報告あり。W01～W20の個々の操作・結果・PID・採証までは報告されていないため、個別全件合格としない。31件の回帰検査の実行結果も未取得。正常終了・再起動・多重起動・ログオン時起動／無効化・異常終了・停止期限・狭小画面・アイコンキャッシュ等は個別証跡を必要に応じて確認する。作業環境からWindowsで独立再検証は未実施。完了後の再試行・問題再現時に本手順を利用できるよう、Git取得操作も含め維持する。
