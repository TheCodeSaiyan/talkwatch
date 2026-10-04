# Data protection and recording

!!! warning "Not legal advice"
    This page says what TalkWatch holds and what it gives you to handle it. What the law asks of you depends on where
    you are and what you do; check with someone qualified.

## What TalkWatch holds

- **Call records:** time, direction, numbers, caller names as the console has them, who answered, duration, and what happened to the call.
- **Recordings and voicemail**, copied off the console.
- **Transcripts**, when Talk's AI transcription is on: what each person said, with Talk's summary and its rating of the call. Copied unless `Talk__CopyTranscripts` is `false`.
- **Console responses** as they came, kept to diagnose changes in the console's API.
- **Alerts**, whose messages carry the caller's number.
- **Accounts:** usernames, email addresses, password hashes, two-factor keys, grants and roles on numbers; a reporting address where one is set; which Talk user each person is on the phone system, and whose outside phone they carry, where an admin links them; and the push subscriptions of browsers they've allowed desktop notifications in.
- **Report copies**, each as it was when it ran, with the callers' numbers it mentions and, when the report includes it, the period's call list.
- **An audit log** of who changed what, who played which recording, who read which transcript, and who exported what.

It all stays on your server. TalkWatch sends data out only where you point it:

- **alerts** to the channels you set up, including what a flow's notify step adds (Talk's summary, the transcript, the voicemail) where the channel's owner may read or hear it themselves;
- **Talk's contacts**, when a flow notifies one: by email, at the address *Talk* holds for them, so a change in Talk changes where it goes;
- **report copies**, emailed to the people and email channels each report is for;
- **desktop notifications**, through the push service of the browser that allowed them (Google's, Mozilla's or Apple's), encrypted so the push service can't read them;
- **traces and metrics** to a collector, if you configure one, with callers' details kept out.

## What it gives you

- **Access by line and by number**, so each person sees only the calls they're granted or hold a role on, and recordings, voicemail and transcripts only with the matching tick.
- **Retention** presets and a sweep that removes calls, audio, transcripts, stored responses, alerts and report copies past their period, with a minimum and a legal hold where records must be kept. See [Retention](retention.md).
- **An audit trail** of plays, exports and changes.
- **Export**, for a request to see what's held about a caller, limited to what the person running it may see.

## Recording calls

Recording is switched on in Talk, not TalkWatch; TalkWatch only copies what Talk records. If you record calls, you'll usually need to tell callers so, for example in the switchboard greeting. The rules differ between countries and between business and personal calls.
