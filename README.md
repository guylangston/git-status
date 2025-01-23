# git-status

> Fast recursive scan for all git repos. Async `git fetch && git status`

## Feature List - CLI

- Terminal Rendering
    - [x] Dynamic rendering for table
    - [x] 16-color support
    - [ ] 256-color support
    - [ ] fast rendering (ncurses, etc)
    - [ ] Detect non-interactive then drop colors
- Git commands
    - [x] `git fetch`
    - [x] `git pull` only if behind and not dirty
    - [ ] `git remote` icons for github, etc
- [ ] Export to json
- [ ] Async scan phase
- [ ] Create a detailed markdown report will all git output
    - [ ] Open report in browser?
- [ ] `--exclude path,path,path`
- [ ] `--help` text and man-style doc file

## Wish List (not current plan for implementation)

- User interaction - TUI
    - Move Up/Down, Scroll Up/Down
    - Sidebar-style flyout with more information
    - Select a git repo and open in Explorer or terminal
- Logger: write all `git` output to a file

## Project Tasks
- Manual Publish
    - [ ] Publish linux release on GitHub
    - [ ] Publish windows release on GitHub
    - [ ] Publish to arch `AUR`
    - [ ] Publish to windows `scoop` package manager
- Automatic Publish process

# Command line options

```bash
    --no-fetch *                # dont `git fetch` before `git status`
    --no-fetch path,path        # same as above, but only on matching path
    -p --pull                   # pull (if status is not dirty)
    --exclude path,path      # dont process repos containing these strings
    -s --summary                # display a summary at the end
```
