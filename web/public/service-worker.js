// Installability, and nothing else.
//
// A browser only offers "install as an app" when a page has a manifest and a service worker with a
// fetch handler. This is that handler and no more: every request goes to the network untouched.
//
// Caching would be actively wrong here. The studio reads live databases; a cached answer is a lie
// about what is in them, and a cached bundle would keep serving an old studio against a new server.
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", event => event.waitUntil(self.clients.claim()));
self.addEventListener("fetch", () => { /* the network answers, as it would without me */ });
