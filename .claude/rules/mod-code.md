---
paths:
  - "Scripts/**/*.cs"
  - "Editor/Verify/**"
  - "Editor/LogicTests~/**"
---

# 모드 코드에서 지킬 것

- `API.Audio.PlaySfx`에는 `SfxTableID.<이름>`을 넘긴다. `(int)SfxID.<이름>`은 컴파일·실행·로그가 다 멀쩡한데
  소리만 안 난다. 이름이 비슷한 타입을 캐스팅해 넘길 때는 SDK 예제가 무엇을 넘기는지 먼저 본다.
- 화면에 놓는 트랜스폼 위치는 매 프레임 `EntityMonoBehaviour.ToRenderFromWorld`를 거친다. 플레이어 위치를
  시뮬레이션 좌표와 비교할 때는 `WorldPosition`을 읽는다. 그려진다는데 안 보이면 외형보다 좌표계부터 본다.
- 보호·해제 검사를 새로 쓸 때 확인한다: 때린 뒤 아무것도 안 하고 기다렸다 보는 구간이 있는가,
  "안 부서진다" 케이스마다 "풀리면 부서진다" 대조 짝이 있는가.
- 새 검사는 먼저 순수 함수로 판정할 수 있는지 본다. 그렇다면 인게임 자가 테스트가 아니라 `Scripts/Logic` +
  `Editor/LogicTests~`에 넣는다. 사람 접속은 게임만 답할 수 있는 것에 아낀다.
