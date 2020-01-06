# i-come-bearing-presence

An idea for publishing presence data from Teams to various subscribers. This way a single polling mechanism can send to _n_ clients with a single poller. Eventually presence will get push/change notification support in graph.

# AAD requirements
You'll need the `Presence.Read` permission, which requires admin consent - something that may be difficult to come by. 

This example is slightly different in that the function itself is handling the interactivity & token acquisition, caching tokens in MSAL in Table storage. The authorization URL is generated, including `offline_access` to get a refresh token, the user is redirected, then we consume the authorization_code via msal on the return trip. No implicit! This is all form_post.

Once the tokens are acquired, it starts polling every 30 seconds for new changes to presence. There's a balance here - 30 seconds is sorta slow but running a timer function every second might be more than you're willing to spend ($0.40 at last check, for ~2.6m executions). Cost consideration is probably more important around storage txns rather than function execution, since msal's token cache accessors access more than once on a single retrieval - so each run is ~3-4 storage txns.

More on the token cache - the token cache should be _per user_ in msal, but we lack an effective way to know the user without a hydrated `Account` - it's sort of chicken and egg, although not really - things like `login_hint` could be used, since this is background work that doesn't have a user issuing actual requests to it - but more on that later.
