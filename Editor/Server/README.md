# 데디케이티드 테스트 서버 스크립트

이 폴더는 `D:\NoBreakZoneServer\`에 있는 스크립트의 **원본**이다. 서버 자체(SteamCMD로 받은 실행 파일,
세이브, 로그)는 저장소에 없다 — 수 GB이고 재설치가 한 줄이라 추적할 이유가 없다. 하지만 **스크립트는
추적해야 한다.** 2026-09-08까지 이 파일들은 어디에도 기록돼 있지 않았고, 그 머신이 사라지면 데디서버로
자가 테스트를 돌리는 방법 자체가 같이 사라지는 상태였다.

## 파일

| 파일 | 하는 일 |
| --- | --- |
| `start-server.ps1` | **판을 버리고** 서버를 띄운 뒤 그대로 둔다. 사람이 접속해 있을 때 쓰는 것 |

`D:\NoBreakZoneServer\`에는 이 밖에 `join-and-verify.ps1`(타임아웃되면 서버를 죽인다 — 사람이 서 있는
동안 죽인 적이 있어서 `start-server.ps1`이 생겼다)과 `run-selftest.ps1`, `README-DELETE-ME.md`가 있다.
필요해지면 여기로 들여온다.

## 동기화

편집은 **여기서** 하고, 서버 폴더로 복사한다.

```powershell
Copy-Item "C:\Unity\CoreKeeper\Assets\NoBreakZone\Editor\Server\start-server.ps1" `
          "D:\NoBreakZoneServer\start-server.ps1" -Force
```

## 쓰는 법

```powershell
powershell -File D:\NoBreakZoneServer\start-server.ps1
# 게임 -> 멀티플레이 -> Game ID: NoBreakZoneTestServer1 로 접속해서 가만히 있는다
# 판정: D:\NoBreakZoneServer\data\selftest.log 의 [NBZTEST] 줄, 1분마다 한 판
```

멈출 때는 `Stop-Process -Name CoreKeeperServer -Force`. (스크립트 주석이 예전에 `stop-server.ps1`을
가리켰지만 그런 파일은 없다.)

## 판을 매번 버리는 이유

자가 테스트는 **설계상 파괴적이다** — 벽을 폭파하고, 땅을 파헤치고, 파일런을 세운다. 그런데 세이브는
남는다. 2026-09-08 판정 때 이 월드에는 **켜진 파일런이 367개** 있었고, 그래서
`release-on-switch-off`가 통과할 수 없었다 — 하나를 꺼도 366개가 같은 벽을 덮으니까.

이전 실행의 잔해 위에서 도는 테스트는 재현성이 없다. 그래서 `start-server.ps1`이 띄우기 전에
`data\worlds\`를 비운다. 잔해를 일부러 들여다보고 싶으면 `-KeepWorld`를 넘긴다.

모드 쪽에서도 같은 문제를 막는다 — `NoBreakZoneSelfTestSystem`이 자기가 세운 파일런을 런이 끝날 때
회수하고, 파일런 엔티티가 하나라도 있으면 새로 짓지 않는다.

## 사람이 필요한 이유

플레이어가 0명이면 데디서버는 월드를 메모리에 안 올린다. 그러면 테스트가 딛고 설 땅이 없다 — 모든 타일이
기본값(`{tileset=2, tileType=wall}`)으로 읽힌다. **한 명이 접속해 있기만 하면 된다.** 뭘 할 필요는 없다.
