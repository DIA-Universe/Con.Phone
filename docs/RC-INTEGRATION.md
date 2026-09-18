# Regression candidate integration

Main advanced to 3a1d4b515a2b78ecc38e52a784655506f8f5a2a1 (PR #8) during this review. Its diff was inspected before reconciling the candidate; main itself has not been overwritten.

The standard-control theme from #8 is retained verbatim. The candidate deliberately uses the more extensively tested keyboard router, separate STA clipboard implementation and native-write serialization. The new System Power report in #8 is not carried into this candidate: it restores the two-report 0.1.6 map to avoid expanding the already-working HID service, and uses an opt-in existing-keyboard Meta+L request instead. This is a compatibility choice in the RC, not a claim that #8's System Power implementation was hardware-tested or disproved.

The candidate does not automatically resume remote input or automatically trust a new session after a physical disconnect. Those differences are visible for review and are not silently merged into main. Filename/InformationalVersion must identify this candidate as regression RC rather than the unrelated main-branch 0.2.1 build.
