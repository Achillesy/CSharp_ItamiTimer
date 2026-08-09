ItamiTimer — the itami command line tool
========================================

This folder contains two programs:

  ItamiTimer.exe   the timer itself (the one on your Start menu)
  itami.exe        a command line tool, described below

You never have to use itami.exe. It exists for one job the window deliberately
has no buttons for: choosing, and testing, the command the alarm runs.


What the alarm can run
----------------------

rules.json (in %LOCALAPPDATA%\ItamiTimer\) can hold a shortlist of shell
commands under "executeCommand". When the Execute switch in Settings is on and
the alarm goes off, ItamiTimer runs THE FIRST ONE — always #0, never any other.

The list is a shortlist, not a menu: to change which command is armed, you move
one to the top.


The three commands
------------------

Open a terminal in this folder (Shift + right-click here -> "Open PowerShell
window here"), then:

  .\itami.exe commands --list
        Print the list. The * marks #0 — the one the alarm will run.
        Changes nothing.

  .\itami.exe commands --select 3
        Move entry 3 to the top, so it becomes #0.
        Rewrites rules.json and keeps a .bak copy next to it. Your comments and
        indentation survive exactly as you wrote them.
        A running ItamiTimer picks this up on its own — no restart needed.

  .\itami.exe commands --execute
        Run #0 right now, so you can see whether it actually works without
        waiting for an alarm. Asks y/N first, because that list usually has
        "shut down" and "restart" in it.

Anything else — a misspelled switch, --select with no number, a number that
isn't in the list — just prints the list and changes nothing. Only those exact
forms do anything, so a typo can never run or rewrite something by accident.


When a command seems to do nothing
---------------------------------

The alarm runs your command and writes everything it can see — exit code,
output, errors — to itami.log. No window opens.

There is one class of failure it cannot see: a very few commands report failure
only to a console. On a machine where hibernation is turned off, "shutdown /h"
exits reporting success, prints nothing a program can capture, and simply does
nothing at all. That is one branch of one command, not a general rule — unknown
commands, bad paths and invalid flags all report normally.

So if a command appears to have done nothing, and the log only says
"exited with 0", run it yourself from a terminal:

    .\itami.exe commands --execute

There you have a real console, and the message that got swallowed shows up.
(For that hibernate example it reads "hibernation has not been enabled" — the
fix is to run "powercfg /h on" from an administrator terminal.)


Where things live
-----------------

  %LOCALAPPDATA%\ItamiTimer\rules.json      your goals and executeCommand list
  %LOCALAPPDATA%\ItamiTimer\settings.json   sounds, switches, window position
  %LOCALAPPDATA%\ItamiTimer\itami.log       what happened, and why

The window itself never explains anything and never shows a report — that is
deliberate. itami.log is where you look afterwards.
