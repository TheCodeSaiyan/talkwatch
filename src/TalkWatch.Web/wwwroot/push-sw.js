// TalkWatch's service worker: shows a desktop notification for each alert pushed to this browser, and opens the alert's
// page when it is clicked. It does nothing else: no caching, no offline pages.
self.addEventListener('push', event => {
    const alert = event.data ? event.data.json() : { title: 'TalkWatch', body: '' };
    event.waitUntil(self.registration.showNotification(alert.title, {
        body: alert.body,
        tag: alert.tag,
        renotify: true,
        data: { url: alert.url || '/alerts/inbox' },
    }));
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    event.waitUntil(self.clients.openWindow(event.notification.data.url));
});
