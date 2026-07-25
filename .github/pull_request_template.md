## Summary

## Linked issue

- Closes #

## Scope

- [ ] Single-responsibility change
- [ ] No unrelated refactors included

## Testing

- [ ] `dotnet build Trackdub.slnx -m:1 -p:Platform=x64`
- [ ] `dotnet test Trackdub.slnx -m:1 -p:Platform=x64`
- [ ] Targeted tests only
- [ ] Not run, with justification below

### Test notes

## Architecture review

- [ ] No layer dependency violation introduced
- [ ] No inference code added to `Trackdub.App`
- [ ] No persistence added to view models
- [ ] No pipeline truth moved into UI state
- [ ] No state mutation added from model wrappers

## License/model impact

- [ ] No new third-party dependency
- [ ] No new model or model asset
- [ ] New dependency/model documented
- [ ] Manifest/license requirements reviewed
- [ ] Commercial-safe mode impact reviewed

## Risk and rollback

- [ ] Low-risk change
- [ ] Rollback is straightforward
- [ ] Follow-up work required, noted below

## Milestone notes

## Agent notes