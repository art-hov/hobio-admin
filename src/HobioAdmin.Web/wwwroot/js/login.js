// Hobio Admin sign-in. Firebase signs the user in in the browser; the ID token is then posted to the
// server (/auth/session), which verifies it with the Firebase Admin SDK and checks the admin allow-list.
import { initializeApp } from "https://www.gstatic.com/firebasejs/11.0.0/firebase-app.js";
import {
    getAuth,
    GoogleAuthProvider,
    inMemoryPersistence,
    setPersistence,
    signInWithEmailAndPassword,
    signInWithPopup,
    signOut,
} from "https://www.gstatic.com/firebasejs/11.0.0/firebase-auth.js";

const configElement = document.getElementById("firebase-config");
const errorElement = document.getElementById("login-error");
const sessionForm = document.getElementById("session-form");
const tokenInput = document.getElementById("idToken");

const app = initializeApp({
    apiKey: configElement.dataset.apiKey,
    authDomain: configElement.dataset.authDomain,
    projectId: configElement.dataset.projectId,
});
const auth = getAuth(app);

// Don't keep a Firebase session in the browser: the portal's own cookie is the session.
await setPersistence(auth, inMemoryPersistence);

const messages = {
    "auth/popup-closed-by-user": "The sign-in window was closed before finishing.",
    "auth/cancelled-popup-request": "The sign-in window was closed before finishing.",
    "auth/popup-blocked": "Your browser blocked the sign-in window. Allow pop-ups for this site and try again.",
    "auth/invalid-credential": "Wrong email or password.",
    "auth/invalid-email": "Enter a valid email address.",
    "auth/user-disabled": "This account is disabled.",
    "auth/too-many-requests": "Too many attempts. Wait a moment and try again.",
    "auth/unauthorized-domain": "This site's domain isn't authorised in Firebase. Add it under Authentication > Settings > Authorized domains.",
    "auth/network-request-failed": "Network error. Check your connection and try again.",
};

function showError(error) {
    errorElement.textContent = messages[error?.code] ?? "Sign-in failed. Please try again.";
    errorElement.hidden = false;
    setBusy(false);
}

function setBusy(busy) {
    document.querySelectorAll(".login-button").forEach((b) => (b.disabled = busy));
}

async function complete(credential) {
    const idToken = await credential.user.getIdToken(true);
    await signOut(auth);
    tokenInput.value = idToken;
    sessionForm.submit();
}

document.getElementById("google-sign-in")?.addEventListener("click", async () => {
    errorElement.hidden = true;
    setBusy(true);
    try {
        const provider = new GoogleAuthProvider();
        provider.setCustomParameters({ prompt: "select_account" });
        await complete(await signInWithPopup(auth, provider));
    } catch (error) {
        showError(error);
    }
});

document.getElementById("email-sign-in")?.addEventListener("submit", async (event) => {
    event.preventDefault();
    errorElement.hidden = true;
    const email = document.getElementById("email").value.trim();
    const password = document.getElementById("password").value;
    if (!email || !password) {
        showError({ code: "auth/invalid-email" });
        return;
    }

    setBusy(true);
    try {
        await complete(await signInWithEmailAndPassword(auth, email, password));
    } catch (error) {
        showError(error);
    }
});
