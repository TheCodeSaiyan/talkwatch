# Voicemail that can't wait

A voicemail is a caller who's done their part: they've said what they want, and
now they're waiting. Most can wait an hour. The trouble is the one that can't,
sitting among the ones that can, in a mailbox nobody checks until lunch.
TalkWatch can tell someone the moment a message is left, send it to them with
what was said, and treat the urgent ones differently.

## Two triggers, a minute or so apart

A flow can start on either:

- **New voicemail**, as soon as the caller has left a message.
- **Voicemail transcribed**, when Talk's transcript of it arrives, usually a
  little after the voicemail itself. It needs Talk's AI transcription to be on.

Use **New voicemail** to hear about it fast. Use **Voicemail transcribed** when
the flow needs to know what was said, either to send it or to decide on it,
since **New voicemail** runs before Talk has written it.

## Sending the message itself

A notify step can send Talk's **summary**, **what was said**, and the
**voicemail**, each to a channel whose owner could read or hear it in TalkWatch
themselves, and only once it exists:

| Channel | Gets |
|---|---|
| Email | the text in full, and the voicemail attached up to 10 MB; a longer one is a link to play it |
| ntfy, Telegram | the text, trimmed to fit, and a link to play the voicemail |
| In TalkWatch | the summary on the desktop notification |
| Webhook | `summary`, `transcript` and `voicemail` fields |

So someone on the move can hear the message on their phone without signing in,
and someone without access to that line's voicemail gets the alert without the
audio, rather than a way round their access.

## Picking out the urgent ones

The demo starts with a flow called *Voicemail that sounds urgent*:

```text
When    Voicemail transcribed
Branch  on the voicemail says "urgent" or "emergency" or "today"
  If so       Notify  the manager, urgent, with what was said and the voicemail
  Otherwise   Notify  the manager, with the summary
```

The **If it says** condition matches whole words in any case. Urgent goes at
high priority where the channel has one, ntfy's `high`, and goes through quiet
hours, so the 07:30 *my appointment's today* reaches someone before 09:00.

**If it says** is for admins only, and the editor warns why: a flow that tests
what a voicemail says tells whoever it notifies that those words were said, even
though the alert never quotes the voicemail. A word list is a judgement about
callers, so it's worth trying on the last week (**Try it on the last 7 days**)
before switching it on, to see how often *today* turns up in a message that
isn't urgent at all.

## Gathering the rest

On a busy line, a phone that buzzes for every voicemail ends up ignored. The
demo's *Voicemail, gathered* flow puts a **Bundle** step first:

```text
When    New voicemail
Bundle  for 15 minutes
Notify  the front desk
```

Everything the flow sends to each channel in those 15 minutes arrives as one
message (*3 × New voicemail*), and any acknowledged in the meantime are left
out. A bundle can gather for up to 240 minutes.

![The Flows workspace: each flow drawn as the diagram it runs](../images/flows.png)

## Voicemail Talk calls answered

When a call is forwarded to someone's mobile and the mobile's own voicemail
takes it, Talk logs the call as answered. TalkWatch can catch those from the
greeting in the transcript and turn them into voicemail, so they raise **New
voicemail** like any other. See [Staff who answer on their
mobiles](mobiles.md#calls-a-mobiles-voicemail-took).

## Try it in the demo

Play **Voicemail** from **Try a scenario** and watch it reach **Recent
activity** on **Now** as voicemail. Then open *Voicemail that sounds urgent*
under **Flows** to see the branch, and change the words to try your own.

## See also

- [Alerts](../alerts.md#sending-the-summary-what-was-said-and-the-voicemail),
  for what each channel carries.
- [Signing in and access](../access.md#lines-and-grants), for who may hear a
  voicemail and read a transcript.
