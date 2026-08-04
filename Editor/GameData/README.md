# 게임 오브젝트 데이터 (자동 추출)

게임 전체 오브젝트 DB를 한 번에 뜯어 만든 정적 참조표. **추측·반복테스트 대신 이 표를 보고 설계한다.**

- `object_flags.csv` — 유니크 ObjectID **2279개** × 컴포넌트 유무 플래그
- 추출: `NoBreakZoneDatabaseDumpSystem`이 로드 시 `PugDatabase.objectInfos` 전체를 순회하며 각 프리팹에 `HasComponent<T>`를 찍어 `Player.log`에 덤프 → CSV로 정리
- 컴포넌트 **이름**은 릴리즈 빌드에서 못 읽음(DebugTypeName 스트립 + 모드에서 리플렉션 금지). 그래서 **이름이 아니라 "관심 컴포넌트 유무"** 를 찍는다.

## 컬럼

| 컬럼 | 컴포넌트 | 의미 |
| --- | --- | --- |
| id | — | ObjectID |
| type | (EntityObjectInfo.objectType) | ObjectType (PlaceablePrefab=설치물 등) |
| tileType | (EntityObjectInfo.tileType) | 타일 종류 (none/wall/ore/floor…) |
| health | HealthCD | 체력 있음(파괴 파이프라인 대상) |
| damageable | DamageableObjectCD | |
| destructible | DestructibleObjectCD | **월드 파괴물**(광석·항아리·배럴·고대파괴물) 표식 |
| dontDestroy | DontDestroyOnZeroHealthCD | 0체력에도 파괴 안 됨(기본 보유 여부) |
| immune | ImmuneToDamageCD | |
| lootTable | DropsLootFromLootTableCD | **전리품 테이블 드롭**(항아리·고대파괴물·벽) |
| lootOnDmg | DropsLootWhenDamagedCD | **피격 시 전리품 드롭**(광석 덩어리) — 자원복제 핵심 |
| dontDropSelf | DontDropSelfCD | 자기 자신을 안 떨굼 (745/747이 보유 → 판별력 없음) |
| dontDropLoot | DontDropLootCD | |
| mineable | MineableCD | 곡괭이 대상. **설치물 대부분 보유**(회수용) → 판별력 없음 |
| diggable | DiggableCD | 삽 대상. 전선 등 설치물도 보유 → 판별력 없음 |
| requiresDrill | RequiresDrillCD | 드릴 필요(광석) |
| tileCD | TileCD | 타일 엔티티(벽·바닥·지형) |
| plant | PlantCD | |
| growing | GrowingCD | 성장 중(씨앗/작물). 이들은 **health=0** |
| critter | CritterCD | |
| catTags | ObjectCategoryTagsCD | 카테고리 태그 보유 |

## 확정된 보호 판별 규칙 (No Break Zone)

전체 2279개로 검증한 결과 (`기획서 §6` "부수면 자기 자신이 돌아오는가"와 일치):

**보호(PROTECT)** = `type==PlaceablePrefab` **AND** `health==1` **AND** `tileCD==0` **AND NOT(`destructible` OR `lootTable` OR `lootOnDmg`)**

→ 569개 보호. 상자·조명·가구·받침대·보스상자 등 설치물 전부 포함.

**제외(SKIP)** = `destructible` 또는 `lootTable` 또는 `lootOnDmg` 중 하나라도 보유, 또는 TileCD.
→ 모든 `*OreBoulder`·`*Destructible`·항아리·크리스탈·`Wall*Block`(벽) 제외. **광석 누출 0 = 자원복제 안전.**

### 검증으로 확인된 사실
- `MineableCD`·`DiggableCD`·`DontDropSelfCD`는 설치물도 대부분 보유 → **판별자로 못 씀** (초기 시행착오. `mineable` 기준으로 짰다가 상자가 전부 제외됐었음).
- 씨앗·작물은 `health=0`이라 애초에 보호 쿼리(health 필요)에 안 걸림 → 수확 안전.
- 나무·덤불·풀·트로피 ~123개는 과보호되지만 자원복제 아님(4단계 검증엔 무해). 파일런 범위 도입 후 재검토.

## 재생성 방법
`NoBreakZoneDatabaseDumpSystem`을 포함해 빌드→설치→게임 1회 로드→월드 진입.
`Player.log`의 `[NBZDB]` 라인을 뽑아 CSV로 저장. 게임 업데이트로 오브젝트가 바뀌면 다시 뜬다.
