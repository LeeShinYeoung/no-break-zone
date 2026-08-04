---
name: land
description: 피처 브랜치 작업을 끝내고 main에 넣는다. PR을 만들고 머지하고 브랜치를 정리한다. "PR 올려줘", "머지해줘", "이거 끝났어", "랜딩" 같은 요청에 사용한다.
---

# land

피처 브랜치 하나를 **PR 생성 → 머지 → 정리**까지 한 번에 끝낸다.
이게 `main`에 코드가 들어가는 **유일한 경로**다.

## 절대 하지 않는 것

- `--squash` · `--rebase` 머지 — 피처 안의 커밋이 `main` 이력에 남아야 한다
- `main`에서 직접 실행
- deploy(mod.io·창작마당 업로드) 관련 동작 일체 — `/release` 이후 사람이 한다
- 검증했냐고 되묻기 — 호출한 것 자체가 판단이다
- PR 본문에 확인하지 않은 검증 내용을 적기

---

## 1. 전제 확인

```bash
git branch --show-current
git status --porcelain
git log --oneline main..HEAD
```

- **`main`이면 중단한다.** "피처 브랜치에서 실행해야 합니다"라고 알리고, 어떤 브랜치를
  땄어야 했는지 제안한다. 되돌리려면 사람이 판단해야 하므로 임의로 브랜치를 만들지 않는다
- **작업 트리가 더러우면** 무엇이 남았는지 보여주고 커밋할지 물어본다
- **`main..HEAD`가 비어 있으면** 머지할 게 없다. 중단한다

### `main`이 원격과 어긋나 있는지 — 놓치면 PR이 오염된다

PR은 **원격** `main`과 비교된다. 로컬 `main`이 앞서 있으면 그 커밋들까지 PR에 딸려 들어간다.
이 피처와 무관한 변경이 리뷰 대상이 되고 머지 커밋에 묶인다.

```bash
git fetch -q origin
git log --oneline origin/main..main   # 로컬만 있는 것
git log --oneline main..origin/main   # 원격만 있는 것
```

| 결과 | 대응 |
| --- | --- |
| 둘 다 비었다 | 정상. 진행한다 |
| `origin/main..main`에만 있다 (fast-forward) | 커밋 목록과 이유를 **보고한 뒤** `git push origin main` 하고 진행한다 |
| 양쪽 다 있다 (diverged) | **중단한다.** 자동으로 rebase·merge하지 않는다. 상태를 보고하고 사람의 판단을 받는다 |

## 2. 검증 상태 파악 — 묻지 않는다

**`/land`를 부른 것 자체가 "머지해도 된다"는 판단이다.** 다시 확인받지 않는다.

검증 상태는 **이미 있는 근거에서 읽어낸다.**

| 근거 | 읽는 법 |
| --- | --- |
| 커밋 메시지 | `미검증` 표기가 있는지 (§8 규칙상 미검증이면 적혀 있다) |
| 대화 맥락 | 이번 세션에서 사람이 게임에서 확인했다고 말한 내용 |
| 변경 내용 | 문서·설정만 바뀌었으면 게임 검증이 애초에 무의미하다 |

읽어낸 것을 PR 본문에 그대로 적는다. **근거가 없으면 "이 PR에는 인게임 검증 기록이 없다"고
적는다. 지어내지 않는다.** 머지는 그대로 진행한다.

## 3. 푸시

```bash
git push -u origin "$(git branch --show-current)"
```

## 4. PR 생성

**본문 형식의 원본은 `.github/pull_request_template.md` 하나뿐이다.**
이 스킬에 절 제목이나 골격을 옮겨 적지 않는다 — 두 곳에 있으면 반드시 어긋난다.

```bash
cat .github/pull_request_template.md
```

읽은 다음:

1. **절 구조를 그대로 유지한다.** 제목·순서·개수를 바꾸지 않는다
2. 각 절의 `<!-- -->` 주석이 무엇을 쓸지 지시한다. **그 지시를 따르고, 주석은 지운다**
3. 재료는 `git log main..HEAD` 와 2단계에서 읽어낸 검증 상태다
4. 템플릿이 바뀌면 이 스킬을 고치지 않아도 결과가 따라간다

본문을 임시 파일에 쓰고 `--body-file`로 넘긴다. 긴 본문을 `--body "$(...)"`로 넘기면
따옴표·백틱에서 깨진다. **저장소 안에 임시 파일을 만들지 않는다** — 루트가 곧 modPath다.

```bash
BODY="$(mktemp -t nbz-pr)"
# ... 채운 본문을 $BODY 에 쓴다 ...
gh pr create --base main --title "<읽히는 한 문장>" --body-file "$BODY"
rm -f "$BODY"
```

### 제목 — 서술형 한 문장. `type:` 접두사를 붙이지 않는다

```
○  Adopt a feature-branch and PR workflow
○  Narrow protection to the pylon radius
✗  chore: adopt a feature-branch and PR workflow
```

접두사가 필요한 건 **squash merge**를 쓰거나 릴리스 자동화가 제목을 파싱할 때다.
우리는 둘 다 아니다. merge commit에서 PR 제목은 커밋 subject가 되지 않는다 —
subject는 GitHub이 `Merge pull request #N from …`으로 만들고 PR 제목은 본문 첫 줄로 간다.

커밋에는 이미 전부 `type:`이 붙어 있으므로 PR 목록에서 한 번 더 반복할 이유가 없다.
제목의 독자는 PR 목록을 훑는 사람이지 파서가 아니다.

**제목과 본문 모두 영문이다** (`CLAUDE.md` §1-1).

## 5. 머지

```bash
gh pr merge --merge --delete-branch
```

저장소 설정이 `delete_branch_on_merge: false`라 `--delete-branch`를 **반드시** 붙인다.
붙여도 원격만 지워질 수 있으니 다음 단계에서 로컬을 확인한다.

## 6. 정리

`gh pr merge --delete-branch`가 **로컬과 원격 브랜치를 둘 다 지우고** `main`으로 옮겨준다
(도움말: `Delete the local and remote branch after merge`). 남는 건 remote-tracking ref뿐이다.

```bash
git fetch --prune origin     # 실제로 필요한 건 이것 하나
git branch -a                # main 만 남았는지 확인
```

`checkout`·`pull`·`branch -d`를 다시 하지 않는다. 이미 끝난 일이라 no-op이거나 에러가 난다.

**피처 브랜치가 아직 보이면** 머지가 실제로 안 된 것이다. 강제 삭제(`-D`)하지 말고
`gh pr view`로 상태를 확인해 보고한다.

## 7. 보고

- PR 번호와 URL
- `main`에 들어간 머지 커밋
- 브랜치 정리 결과
- **`Editor/Docs/status.md` 갱신이 필요한지 판단**해서 필요하면 알린다.
  기능 단계가 진행된 PR이면 대개 필요하다

---

## 실패했을 때

| 증상 | 대응 |
| --- | --- |
| `gh pr create` 가 "no commits between" | 브랜치가 `main`과 같다. 커밋했는지 확인 |
| 머지 충돌 | 자동 해결하지 않는다. 충돌 파일을 보여주고 사람에게 알린다 |
| 정리 후에도 피처 브랜치가 남아 있음 | 머지가 실제로 안 됐다. `gh pr view`로 상태를 확인한다. 강제 삭제(`-D`) 하지 말고 원인을 보고한다 |
| 푸시 거부 | 원격이 앞서 있다. `git pull --rebase` 는 사람 확인 후에만 |
| `main`이 diverged | 1단계에서 걸러진다. 자동으로 rebase·merge하지 말고 상태를 보고한다 |
