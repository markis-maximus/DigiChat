## What this changes

<!-- What behaviour differs after this, and why. -->

## How it was verified

<!-- Which of the checks below you ran, and anything you checked by hand in
     OBS or the admin panel. -->

## Checklist

- [ ] The change is understandable without the private concept document.
- [ ] `scripts\Verify-Repository.ps1` passes (build, tests, both frontends,
      roster checker, public-mode publish and artifact check).
- [ ] LocalDB integration skips, if any, are called out — a skip is not
      verification of the production database path.
- [ ] Visual changes were inspected in mock mode.
- [ ] No secret, viewer data, database, log, generated output, or Digimon
      artwork is tracked.
- [ ] Operator and recovery documentation matches the new behaviour.
- [ ] The server process and port 5170 were released after manual testing, and
      the shared LocalDB instance was not stopped as routine cleanup.
- [ ] Commit author/committer is the identity intended to be public.
