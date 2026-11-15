// Keeps a log scrolled to its newest line while the reader is at the bottom. Scrolling up to read
// stops that; scrolling back to the bottom starts it again. Runs entirely in the page, so a busy log
// costs no calls back to .NET.
const slack = 24;

export function follow(element) {
    if (!element || element.dataset.following !== undefined) {
        return;
    }

    element.dataset.following = "true";
    const atBottom = () => element.scrollHeight - element.scrollTop - element.clientHeight < slack;
    element.addEventListener("scroll", () => {
        element.dataset.following = atBottom() ? "true" : "false";
    }, { passive: true });
    new MutationObserver(() => {
        if (element.dataset.following === "true") {
            element.scrollTop = element.scrollHeight;
        }
    }).observe(element, { childList: true, characterData: true, subtree: true });
    element.scrollTop = element.scrollHeight;
}
