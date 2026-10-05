# Finding your way round

TalkWatch has no sidebar. Along the top runs the rail: one word per workspace
with its live count underneath, so the way to a page is also the reason to go
there. "3 missed today" under **Calls** tells you something before you click.
Everything on every page goes through your own access, so the counts are
yours, not the whole site's.

| Workspace | Under the word | Key |
|---|---|---|
| **Now** | calls in progress | `g` `n` |
| **Operator** | people free | `g` `o` |
| **Calls** | missed today | `g` `c` |
| **Call-backs** | callers to return | `g` `b` |
| **Alerts** | unread alerts | `g` `l` |
| **Flows** | flows switched on | `g` `f` |
| **Analytics** | | `g` `a` |
| **Switchboards** | | `g` `s` |
| **Reports** | | `g` `r` |

**Flows** only shows for people who can set up alerts. **Configure** is a menu
at the end of the rail holding People, Roles, Groups, Numbers, Alert channels,
Outside voicemail, Outside phones and Retention, and it lists only the ones
your role lets you open, so the rail stays the same whoever's looking. Settings
live there rather than on the rail because you visit them once and then leave
them alone.

## Getting about by keyboard

- **Ctrl+K** (or **Cmd+K**), or **/** anywhere outside a text box, opens the
  command palette. It goes to anything the rail offers you, runs a few common
  views by name ("Show missed calls today", "Show callers who hung up in the
  menu"), switches between light and dark, and searches calls, numbers, people
  and lines as you type. Searches run through your access, so it never turns
  up a call you couldn't open.
- **g** then a letter jumps straight to a workspace, using the keys in the
  table. You have just over a second after the **g**, and neither works while
  you're typing in a box.

## Looking at one number

When you can see more than one of the site's numbers, a switcher sits next to
the TalkWatch mark on the rail. Pick a number and every call page narrows to
calls on it: the call log, Now, Operator, Call-backs, Analytics and the rest.
It's done in the same place as access itself, so no page can forget to apply
it. The choice is kept on your account rather than in the browser, so it
follows you to your phone, and the switcher changes colour while it's
narrowed so you don't forget either.

## Looking at one call without leaving the page

Choosing a call on Now or in the call log opens it in a panel on the right,
with the page still there beside it. The panel shows how the call ended (or
how it's going), its line, how long it lasted and how long it took to answer,
the route it took and what happened step by step. **Open** goes to the call's
full page, with its recording and transcript. **Pin** keeps the panel open
while you move between pages; otherwise it closes when you go somewhere else.
**Esc** or the cross closes it. On a narrower screen it sits over the page
instead of beside it. To open a call's full page straight away, open the link
in a new tab as you normally would.

## The status strip

When something needs attention, a strip appears under the rail on every page:

- **Talk's data has changed shape.** A console update changed what Talk
  sends, and TalkWatch has stopped copying new calls rather than store them
  wrong. It says when the console was updated and from which versions to
  which. Nothing's lost; see [Upgrading](upgrading.md).
- **The console couldn't be reached** at the last poll, and what you see is
  from the time it says.
- **An untested Talk or UniFi OS version**, or a pre-release channel, where
  endpoints can still change.

When all's well there's no strip. The freshness dot on the rail says so instead.

## Now

TalkWatch opens on **Now**: what the phone system is doing at this moment.
It's laid out as the calls move through it. Calls stream in to **Live calls**,
from ringing through to connected. When one ends it stays a few seconds,
faded, with how it ended, then travels along its stream (answered, voicemail
or missed) into **Recent activity**, and from there into the **call log** at
the bottom. A missed call's mark also climbs to the rail's "missed today",
which changes when it lands. The streams get busier the more calls took that
path in the last five minutes. Above the lists are today's calls, calls in
progress, the answer rate and missed calls; below them, who's on a call and
which handsets are online. The page stays live, so you can leave it open.

A list holds still while your pointer is over it, or you're tabbing through
it, so nothing moves under a click. Its heading counts what's waiting
("2 waiting · Show"); choose that to catch up at once, or move away and it
catches up half a second later. The figures above never wait. If your system
is set to reduce motion, nothing travels: changes just appear where they land.

The people and handsets come from Talk's live feed. If that's down, the
heading says so and what went wrong, and calls carry on being copied from the
call log each poll.

## Operator

**Operator** is the screen for whoever looks after the phones all day: who's
free, what's ringing, and who's waiting to be called back.

- **Needs someone** comes first: calls ringing or waiting that nobody has
  picked up. Then **In progress**.
- **People** are grouped by ring group, with each person's presence, so you can
  see which team has someone free. With a number chosen in the switcher, only
  the groups and people a call to that number can reach are shown. Paging
  groups never count as teams, since they take no calls.
- Outside numbers that a group or switchboard forwards to, someone's mobile,
  say, are shown as **Outside number · forward** and listed under
  **Forwarded to**. Talk knows nothing about their presence, so they're never
  counted as free.
- **Call-backs waiting** sits on the right, longest first.

TalkWatch reads Talk and never controls it. Answering and transferring stay on
the handset.

## Calls

The call log lists every call you can see, with its outcome worked out from
what happened to it rather than from Talk's status (see the
[home page](index.md) for why). Each filter you add shows as a chip that says
what it does and comes off with one click. The filters live in the address, so
a filtered view can be bookmarked or sent to someone. Someone without the
same access just sees fewer calls.

A call's page tells it as what happened, from Talk's routing events: which
number it came in on, the menu options chosen, who it rang and for how long,
and how it ended. Below that come the recording or voicemail, Talk's summary
and transcript when transcription is on and you're allowed to read it, and
**With this number**, the other calls from the same caller. A forwarded call
that an outside number answered also shows what TalkWatch made of that
number; see [outside voicemail](alerts.md).

**Audio** plays in the page with a waveform. Nothing is downloaded until you
press play, so opening a call doesn't put a play in the audit log. Pressing
play fetches the file once and the waveform is drawn from that same download,
so **one play is one audit entry**. Click a line of the transcript to jump
there; the line being spoken is highlighted. If you move to another page, a
small player keeps the recording going.

A call that's still ringing or sitting in a menu with **nothing new from Talk
for 15 minutes** is treated as over. Talk reports every step of a call while
it's being routed, so that long a silence means its hang-up was never logged,
and without this it would show as live for hours. Any call that started more
than a day ago is taken as finished for the same reason.

## Call-backs

**Call-backs** lists missed callers nobody has got back to in the **last 30
days**, one row per caller however many times they tried. The list is about
people still waiting, and a caller from two months ago is better looked up
than chased; **Include older** shows the lot.

A caller leaves the list when someone rings them back from any phone on the
system, when a later call from them is answered, or when someone marks them
done. The edge of each row turns amber after **4 hours** waiting and red
after **24**.

If your role allows, you can give a caller to one person from the menu on the
row, and the row then shows **Assigned to** them. A flow can do the same
([alerts](alerts.md)). **Got back to lately** underneath shows who was dealt
with in the last week.

## Analytics and Switchboards

**Analytics** holds the trends: calls by outcome, answer rate, busiest hours,
how fast missed callers were got back to, how long phones ring before someone
answers, when calls go unanswered by weekday and hour, and Talk's call-quality
scores.

**Switchboards** shows each auto-attendant: how many callers hang up during
each greeting and how long they last, how long the greeting itself runs, and
what each menu option leads to. If most hang-ups happen in the first fifteen
seconds, that's the greeting to shorten.
