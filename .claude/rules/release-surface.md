---
paths:
  - "README.md"
  - "CHANGELOG.md"
  - "description.txt"
  - "Editor/*.asset"
  - "Editor/genassets.py"
  - "Scripts/Converters/**"
  - "Scripts/NoBreakZoneConfig.cs"
  - "Editor/Docs/art/**"
---

# 플레이어에게 보이는 것을 바꿀 때

- 제작처·조작·설정처럼 플레이어가 읽는 사실이 바뀌면 `README.md`·`CHANGELOG.md`·`description.txt`·
  `genassets.py`의 아이템 문안·`Editor/NoBreakZone_modio.asset`의 `summary`를 한 번에 맞춘다.
  mod.io 웹의 긴 설명은 사람이 고쳐야 한다고 알린다.
- `Editor/NoBreakZone_modio.asset`의 `modId`를 지우거나 0으로 만들지 않는다. 0이면 SDK가 갱신이 아니라
  새 모드 등록으로 동작한다.
- `Editor/NoBreakZone.asset`의 `requiredOn: 3`(ClientAndServer)은 사람이 정한 값이라 바꾸지 않는다.
  0이면 모드 없는 클라이언트가 들어와 파일런을 못 그리고 보호 예측이 어긋난다.
- `Editor/Docs/art/sprites.py`를 다른 Pillow 버전으로 돌리면 픽셀이 같아도 PNG 바이트가 달라진다.
  재실행 뒤에는 픽셀을 비교하고, 안 바뀐 PNG는 되돌린다.
