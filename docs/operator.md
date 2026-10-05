# Operator

![Operator: callers in the main switchboard's menu, each at the option they last chose](images/operator.png)


**Operator** is the screen for whoever looks after the phones all day: where
callers are in the switchboard, who's free, what's ringing, and who's waiting to
be called back.

## The switchboard, live

Each switchboard the chosen number reaches is drawn as the flow its callers
move through: calls coming in, the menu, each option, and where each option
puts calls through to. The streams between them are busier the more of today's
calls took them.

![A caller presses 1 and then 2, and rides from the menu to each option](images/operator-ride.gif)

- Each caller in the switchboard is a card at the stage they last reached, with
  how long they've been there. As Talk reports each key they press, the card
  rides the stream to the option they chose, as calls do on [Now](now.md).
- Where several callers are at one stage, their cards stack: three show and the
  rest are counted. Point at the stack to fan it open and scroll through it.
  Choose a card to open the call beside the page.
- Each stage shows how many calls reached it today. Talk logs nothing when a
  call passes an opening-hours split, so the branches after one carry the
  option's traffic in their streams but show no count of their own.

## Who's free, and who's waiting

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
