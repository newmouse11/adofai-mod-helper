# ADOFAI Mod Helper — 프로젝트 규칙

## Git / GitHub
Why: 사용자가 "업데이트할 때마다 GitHub에 올려"라고 지시함 (전역 CLAUDE.md §12 "Never push"의 이 프로젝트 한정 예외).
- 검증된 업데이트마다 릴리스까지 한다:
  1. `src/AdofaiModHelper/AdofaiModHelper.csproj`의 `<Version>`을 올린다 (버그 수정 = 패치, 기능 추가 = 마이너)
  2. secret-scan → 커밋 → `git tag -a v<Version> -m v<Version>` → `git push origin main --follow-tags`
     (`-a` 없는 일반 태그는 `--follow-tags`로 올라가지 않는다)
  3. GitHub Actions(`.github/workflows/build.yml`)가 태그를 받아 빌드·자체 검사 후 exe와 SHA256SUMS로 릴리스를 만든다.
     태그와 `<Version>`이 다르면 워크플로가 실패한다 (도구의 새 버전 알림이 둘을 비교하기 때문).
- 문서(README 등)·`catalog/mods.json`만 바뀐 경우는 exe가 같으므로 버전·태그 없이 커밋 → `git push origin main`만 한다.
- README는 한국어 `README.md`와 영어 `README.en.md` 두 개다. 하나를 고치면 다른 하나도 같이 고치고,
  버튼 이름은 `Strings.ko.xaml` / `Strings.en.xaml`의 실제 문구와 맞춘다.
- force-push와 히스토리 재작성은 여전히 금지.
- 커밋 작성자는 GitHub noreply 주소만 쓴다: `newmouse11 <77029057+newmouse11@users.noreply.github.com>`
  (저장소 로컬 git config에 설정됨). 개인 이메일이 커밋에 들어가면 공개 저장소에 노출된다.
- 원격: `https://github.com/Newmouse11/adofai-mod-helper` (기본 브랜치 `main`, 2026-10-26 기준 아직 생성 전).
  도구가 `catalog/mods.json`을 이 저장소 main 브랜치에서 읽으므로 경로·브랜치 이름을 바꾸면 `Core/Catalog.cs`의 `RemoteUrl`도 같이 바꾼다.
- `newmouse11/Nslookup_-ADOFAI-Mod-Install`은 **다른 프로젝트**(이전 설치기)다. 이 프로젝트를 거기에 push하지 않는다
  (2026-10-26에 잘못 올렸다가 복원 커밋 92681e7과 v2.0.0 태그 삭제로 되돌림. 합쳤던 상태는 로컬 `backup/pushed-to-old-repo`).

## 검증
- `dotnet build src/AdofaiModHelper -c Release` 경고 0, `tests/SelfTest` 전부 통과 후에만 커밋.
- `catalog/mods.json`을 바꿨으면 `dotnet run --project tests/SelfTest -c Release -- --online`으로 실제 설치·Id 일치까지 확인.
- 사용자의 실제 게임 폴더는 사용자가 요청할 때만 수정한다. 테스트는 임시 폴더에 만든 복사본으로.
