# Windows task execution boundary — Phase 07

Automatic attendance is **in-process** in the WPF desktop host, scheduled by
`EightPmSchedulerService` (legacy technical name) at the independently configured
08:00 local default. Every startup/resume/tick invokes bounded recovery and
calendar closure. No operating-system Task Scheduler registration is installed
or asserted by this phase. If the application is closed, neither scheduled
attendance nor midnight closure can be guaranteed at wall-clock time; the
next successful application start/tick records the actual execution time.
Historical ledgers are retained.

An unattended Windows Task Scheduler execution mechanism would require
deployment, single-writer arbitration, an explicit Windows E2E test, and
operator approval before it could be claimed as a working app-closed feature.
This directory deliberately contains no duplicate unattended attendance job.
