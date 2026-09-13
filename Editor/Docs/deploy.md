# 배포 점검 목록

**deploy는 사람이 한다.** AI는 준비까지 하고 멈춘다 (`CLAUDE.md` §5, `workflow.md` 6장).
release(태그 + 깃허브 릴리스)와 deploy(mod.io·창작마당 업로드)는 다른 일이고, 한 명령에 묶지 않는다.

이 문서는 **확인된 것과 아직 모르는 것을 구분해서** 적는다. 빈칸은 윈도우 세션이 채운다.

---

## 지금 상태 — 관문 둘만 남았다 (2026-09-13)

| 조건 | 상태 |
| --- | --- |
| 기능이 다 있는가 | ✅ 기획서 §4의 네 물건 전부 |
| 빌드가 되는가 | ✅ 계속 `BUILD OK` |
| 인게임에서 도는가 | ✅ 제작·설치·토글·보호·해제·회수까지 사람이 확인 |
| 한국어가 나오는가 | ✅ 슬롯 7 (research.md 28장) |
| 배포 절차를 아는가 | ✅ 조사 완료 → `store.md` |
| 스토어 문안·썸네일 | ✅ `description.txt` · `Editor/Docs/store.md` · `art/store_thumbnail.png` |
| 버전 문자열 | ✅ `0.1.0` |
| **기존 월드에서 로드** | ❌ **사람 필요** |
| **멀티플레이 1회** | ❌ **사람 필요** |

**순서: 빌드 → 체크포인트 통과 → release → deploy.**
`status.md`의 체크포인트 목록이 그 관문이다.

---

## 배포 전에 반드시

- [x] `powershell -File Editor/build.ps1` 종료 코드 0
- [x] `Player.log`에 우리 경고가 없는지 — 없다. `missing: Items` 0건.
      `couldn't load … asmdef` 한 줄은 배포된 다른 모드들도 똑같이 내는 무해한 줄이다
- [x] 컴파일 경고 0 — `FindObjectOfType`의 CS0618을 없앴다
- [ ] 유니티 Test Runner 에서 `NoBreakZone.Tests` 통과 — **라이선스 문제로 못 돌린다**
      (workflow.md). 대신 `logictest.ps1`·`verify.ps1`이 관문이다
- [ ] `status.md` 체크포인트 전 항목 확인 — **1번(자원 복제 없음)이 다른 무엇보다 우선**
- [ ] **기존 월드**에서 로드 — 지금까지 새 월드만 썼다
- [ ] 멀티플레이 1회 — 이 모드는 서버 권한 코드가 많다

## 저장소 쪽 준비물

- [x] `README.md` — 설명·설치·설정
- [x] `LICENSE.md` — MIT
- [x] 버전 문자열 — `0.1.0`
- [x] `CHANGELOG.md`
- [x] 스토어 문안 (`description.txt`, `Editor/Docs/store.md`) 과 썸네일
- [x] **`ModBuilderSettings`를 저장소 안으로** — `Editor/NoBreakZone.asset`. 추적되면서 번들에는
      안 들어간다. `displayName`도 `No Break Zone`으로 채웠다
- [ ] **`requiredOn` 결정** — 지금 `None(0)`이다. 이 모드는 오브젝트를 추가하므로 모드 없는
      클라이언트는 파일런을 그릴 수 없다. `ClientAndServer(3)`가 맞아 보이는데 그러면 모드를
      가진 사람만 접속할 수 있다. **사람이 정할 일이다**

---

## 미확인 — 윈도우에서 채울 것

### 1. ~~한국어 언어 슬롯~~ — 해결 (2026-09-13)

게임 번들(`defaultlocalgroup_assets_all.bundle`)을 풀어 13개 `LanguageDataBlock`을 직접 읽었다.
**한국어는 7번**이고 13개 전부를 `genassets.py`의 `LANGUAGE_SLOTS`에 적어뒀다. 기전은
research.md 28장. 유니티 GUI는 필요 없었다.

### 2. ~~업로드 절차~~ — 조사 완료 (2026-09-13)

**[`store.md`](store.md)에 전부 적혀 있다** — 두 스토어의 단계, 붙여넣을 문안(영/한), 태그,
썸네일 규격. SDK의 업로드 탭을 디컴파일해서 확인한 것이라 추측이 없다.

요점만 여기 남긴다.

- **자동화는 불가능하다.** 업로드 경로가 전부 에디터 창의 버튼 핸들러이고 CLI도 공개 메서드도
  없다. `ModBuilder.BuildMod`만 스크립트로 부를 수 있고 그건 `CliBuild.cs`가 이미 쓰고 있다
- **mod.io는 이름·한 줄 요약·로고·공개여부만 받는다.** 버전도 변경 로그도 안 실린다.
  긴 설명과 태그는 mod.io **웹사이트**에서 따로 채운다
- **창작마당은 모드 루트의 `description.txt`를 자동으로 읽는다.** 그래서 그 파일을 저장소에 뒀다
- ⚠ **GUI에서 `Build Mod`를 한 번 눌러야 한다.** 창작마당 탭은 `ModPaths.asset`의 목록에서
  모드를 고르는데, 우리 빌드는 `CliBuild`로 직접 들어가서 그 목록에 등록되지 않는다.
  지금 비어 있어 **"No built mod found"로 막힌다**
- ⚠ **공개 범위 기본값이 비공개다.** 창작마당은 안 건드리면 `Private`, mod.io 새 프로필도
  숨김으로 생성된다. 둘 다 올린 뒤 공개로 바꿔야 한다

---

## 배포 후

- [ ] `status.md` 갱신
- [ ] 첫 사용자 피드백에서 볼 것: 자원 복제 신고가 있는지 — 있으면 **즉시 내린다**
