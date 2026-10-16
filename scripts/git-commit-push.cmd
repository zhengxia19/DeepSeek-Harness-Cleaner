@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul

rem Save the script directory BEFORE any shift: shift moves %1 into %0
rem and would break %~dp0.
set "SCRIPT_DIR=%~dp0"

rem =====================================================================
rem git-commit-push.cmd - Git commit and push batch script
rem
rem Usage:
rem     scripts\git-commit-push.cmd "commit description" [git push extra args...]
rem
rem Parameters:
rem     %~1  commit description (required), used as git commit -m message
rem     %*   remaining arguments are passed through to git push
rem
rem Behavior:
rem     1. Verify the script runs inside a Git repository
rem     2. git add -A to stage all changes
rem     3. Commit only when there are staged changes; otherwise skip
rem     4. git push -u origin <current branch>
rem
rem Exit codes:
rem     0 success
rem     1 missing commit description (usage error)
rem     2 not a Git repository
rem     3 git add / git commit failed
rem     4 git push failed
rem =====================================================================

set "COMMIT_MSG=%~1"
if "%COMMIT_MSG%"=="" (
    echo [ERROR] Missing commit description.
    echo   Usage: %~nx0 "commit description" [git push extra args...]
    exit /b 1
)

rem Collect optional git push arguments (2nd argument onward)
set "PUSH_ARGS="
shift
:collect_args
if "%~1"=="" goto args_done
set "PUSH_ARGS=!PUSH_ARGS! "%~1""
shift
goto collect_args
:args_done

rem Always operate from the repository root (parent of this script's folder)
pushd "%SCRIPT_DIR%.." || exit /b 2

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Not inside a Git repository.
    popd
    exit /b 2
)

for /f "delims=" %%b in ('git rev-parse --abbrev-ref HEAD') do set "BRANCH=%%b"
echo [INFO] Repository: %CD%
echo [INFO] Branch:     %BRANCH%

git add -A
if errorlevel 1 (
    echo [ERROR] git add failed.
    popd
    exit /b 3
)

git diff --cached --quiet
if errorlevel 1 (
    git commit -m "%COMMIT_MSG%"
    if errorlevel 1 (
        echo [ERROR] git commit failed.
        popd
        exit /b 3
    )
    echo [INFO] Committed: %COMMIT_MSG%
) else (
    echo [INFO] No staged changes, skipping commit.
)

git push -u origin "%BRANCH%" !PUSH_ARGS!
if errorlevel 1 (
    echo [ERROR] git push failed.
    popd
    exit /b 4
)

echo [INFO] Pushed to origin/%BRANCH%
popd
exit /b 0
