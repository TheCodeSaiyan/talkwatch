// Desktop notifications for TalkWatch alerts: registers the service worker and subscribes this browser to its push
// service with TalkWatch's public key, then tells TalkWatch where to send. Loaded by the account page only.
const button = document.querySelector('[data-push-enable]');
const state = document.querySelector('[data-push-state]');

function say(text) {
    if (state) {
        state.textContent = text;
    }
}

function keyBytes(base64url) {
    const padded = (base64url + '==='.slice((base64url.length + 3) % 4)).replace(/-/g, '+').replace(/_/g, '/');
    return Uint8Array.from(atob(padded), c => c.charCodeAt(0));
}

async function current() {
    const registration = await navigator.serviceWorker.getRegistration('/');
    return registration ? registration.pushManager.getSubscription() : null;
}

async function enable() {
    if (await Notification.requestPermission() !== 'granted') {
        say('Notifications are blocked for this site in your browser settings.');
        return;
    }

    const registration = await navigator.serviceWorker.register('/push-sw.js', { scope: '/' });
    await navigator.serviceWorker.ready;
    const { key } = await (await fetch('/account/push/key')).json();
    const subscription = await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyBytes(key) });
    const response = await fetch('/account/push', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(subscription) });
    say(response.ok ? 'Desktop notifications are on in this browser.' : 'TalkWatch could not save this browser; try again.');
}

if (button) {
    if (!('serviceWorker' in navigator) || !('PushManager' in window)) {
        say('This browser cannot show desktop notifications.');
        button.disabled = true;
    } else {
        current().then(s => say(s ? 'Desktop notifications are on in this browser.' : 'Desktop notifications are off in this browser.'));
        button.addEventListener('click', () => enable().catch(e => say(`Could not turn them on: ${e.message}`)));
    }
}
