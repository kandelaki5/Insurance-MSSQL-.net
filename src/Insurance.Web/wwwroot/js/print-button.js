// The print button of a printable document (legacy print-button.tsx).
for (const button of document.querySelectorAll('[data-print]')) button.addEventListener('click', () => window.print());
