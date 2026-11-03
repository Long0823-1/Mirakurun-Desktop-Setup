# Windows直結で同じIPを配布する

TV側をDHCPのままにして、Windowsサービスから予約IPを配布します。TV1台専用です。
WindowsとTVの間をLANケーブルで直接接続してください。ルーターや共有LANには適用しません。

## 操作

1. アプリの **Windows直結設定** を開きます。
2. TVにつないだ物理有線ポートと、TVの有線MACを選択・確認します。
3. **直結IPを固定する** を押し、Windowsの管理者確認を許可します。
4. TVの有線を抜き差しして、DHCPを再取得させます。
5. 表示されたTV用IP、またはTVが公開する `.local` 名へ接続します。

PC側有線には専用の固定IP、TV側にはその隣の予約IPを配布します。
既存経路に重なるネットワークは避けて選ぶため、IPの値はPCによって異なります。
配布するのはアドレスとサブネット・リース情報のみで、ゲートウェイとDNSは配りません。
Wi-Fi・VPNのIP、アダプターメトリック、デフォルト経路は変更しません。

サービスはWindows起動時に自動起動します。セットアップ画面を閉じても配布は継続します。
Windowsが停止・スリープ中はDHCPへ応答できません。既存リースを利用できる期間やTV側の
リンクローカルへの切り替えはTVのクライアントに依存します。

## 対応するTV側

専用LANに対応したTCLTV-Mirakurunが必要です。TVのeth0はDHCPのままです。
以前のMirakurun許可設定が元のLANのみの場合、DHCP取得後にHTTP応答が切断されます。
対応版では、実際にeth0へ割り当てられたプライベート `/30` の2ホストのみを追加許可します。
全ネットワークや任意の `.local` を許可するものではありません。

## 解除・ログ

**解除して自動取得に戻す** で、選択した有線ポートを元のDHCPへ戻します。
導入後に別の手動IPを追加した場合は、その設定を保護するため解除を中止します。
先に変更した設定を確認してください。

- サービス名：`MirakurunDirectDhcp`
- 設定・ログ：`%ProgramData%\MirakurunDesktopSetup\DirectConnection`
- UDP 67番を専用サービスが使用します。別のDHCPサーバーがある場合は導入を中止します。
- 登録したMACと選択した受信インターフェース以外の要求は無視します。
- TVから重複を申告された場合は配布を停止します。配線とログを確認してからサービスを再起動してください。

## 検証範囲

2026-11-03、WindowsとTCL Android TVを実接続して、DHCP DISCOVER/OFFER/REQUEST/ACK、
予約IPでのMirakurun画面・API応答、サービス再起動と再取得、解除と再導入を確認しました。
解除後のPC有線DHCP復元と、他インターフェースのIPv4アドレスを変更しないことも確認済みです。

パケット処理とmDNS・既存セットアップを合わせた42項目のテストに合格。
PC全体の再起動、スリープ復帰、長期間の運転、複数TV、別の物理ポートへの差し替えは未検証です。

実装の根拠：
[DHCP RFC 2131](https://www.rfc-editor.org/rfc/rfc2131.html)、
[Windows IP_PKTINFO / IP_UNICAST_IF](https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ip-socket-options)。
