# 작업 흐름에서 지킬 것

- 빌드 전에 유니티 에디터만이 아니라 **게임 본체와 테스트 서버**가 꺼졌는지 사람에게 묻고 답을 기다린다.
  조용하다고 꺼졌다고 추정하지 않는다. `build.ps1`은 실행 중인 게임과 서버가 읽는 Mods 폴더를 덮어쓴다.
- 재접속해도 인벤토리가 비어 있으면 아이템이 사라진 게 아니라 `lastActiveSession`이 걸린 것이다.
  게임을 완전히 끄고 세이브 4개(`saves/0.json`, `worlds/0.world.gzip`, 각각의 `.pugbackup`)를 복사한 뒤
  `saves/0.json`의 `lastActiveSession`만 `{"x":0,"y":0,"z":0,"w":0}`로 되돌린다. `.pugbackup`은 건드리지 않는다.
- `error CS`가 0개인데 빌드가 실패하면 코드를 고치기 전에 그대로 한 번 다시 돌린다. 유니티
  `TypeDbJsonGenerator`의 간헐 실패다.
- 게임 동작에 기대는 가설은 프리팹 YAML이나 레퍼런스 모드만 보고 세우지 않는다. 디컴파일된 게임 코드를 먼저 연다.
  "레퍼런스에 사례가 없다"는 반증이 아니라 코드를 열어 볼 신호다.
- 조사용 저장소(`Adrriiannn/ck-db`, `Adrriiannn/ck-mods`, `CoreKeeperMods/CoreLib`, `Pugstorm/CoreKeeperModDocs`)는
  스크래치패드에 `--depth 1`로 받는다. 저장소 안에 두면 번들에 실린다. ck-db는 비공식이라 최종 근거는 윈도우 빌드다.
