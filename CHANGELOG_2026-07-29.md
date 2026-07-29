# 2026-07-29 Changelog

## 1. Accumulated minutes in rules.json
- GoalGroup.AccumulatedMinutes (double, minutes)
- Classify returns groupName via out param
- GroupRules.Accumulate() writes on task end
- OnTask only, Neutral excluded

## 2. LF line endings
- .gitattributes: * text=auto eol=lf

## 3. Today tomato count
- GroupRules.TodayTomatoes(): clip to [midnight, now]
- UI: N emoji after each checkbox

## 4. Alarm clock
- Yellow hand (RAlarm=0.62) under hour hand
- Bell button under Pin in toolbar
- Click +5min, long-press accelerates
- Settings: AlarmEnabled, AlarmSound

## 5. D: drive access
- WSL /mnt/d/ via symlink ~/Workspace_01Active
- git.exe with -c http.proxy for GitHub
