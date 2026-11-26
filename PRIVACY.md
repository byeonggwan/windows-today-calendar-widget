# Privacy policy

*Today Calendar widget for Windows* (this app) is an open-source desktop widget. This policy covers the app as published in this repository and its GitHub releases.

## What the app accesses

When you add a Google account, the app asks Google for:

- **Read-only access to Google Calendar** (`calendar.readonly`), to list the calendars you show in Google Calendar and today's events on them.
- **Your email address** (`openid`, `email`), to label the account in the widget.

The app never creates, changes or deletes calendars or events.

## What the app stores, and where

Everything stays on your Windows PC, in `%LOCALAPPDATA%\TodayCalendar`:

- each account's email address and the refresh token Google issued to the app, encrypted with Windows DPAPI so that only your Windows user can read it;
- widget settings such as position, size and theme.

Event details are kept in memory only while the widget shows them. They are not written to disk. When a refresh fails or comes back empty, `error.log` in the same folder records why (the HTTP status, Google's error reason, and how many calendars each account has), never event titles, times or calendar names.

## What the app sends, and to whom

The app talks only to Google (`accounts.google.com`, `oauth2.googleapis.com`, `www.googleapis.com`) to sign in and read your calendars, and to GitHub (`api.github.com`, `github.com`) to check for and download app updates. Update checks send no information about you.

There is no server, analytics, advertising or tracking. The developer never receives your data.

## How to remove access

- Right-click the widget → **Remove account**: the app deletes the account's token from your PC and revokes the app's access at Google.
- You can also revoke access at any time at <https://myaccount.google.com/permissions>.
- Uninstalling: delete `%LOCALAPPDATA%\TodayCalendar`.

## Google API Services User Data Policy

The app's use of information received from Google APIs adheres to the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including the Limited Use requirements.

## Contact

Questions or problems: open an issue at <https://github.com/byeonggwan/windows-today-calendar-widget/issues>.
