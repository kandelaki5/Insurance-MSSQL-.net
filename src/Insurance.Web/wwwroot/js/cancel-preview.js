// The cancellation preview follows the date being entered (UAT H22). The figures for every day the form allows are
// worked out on the server (finance CancelPreview) and embedded in the page; this only shows the one for the date typed:
// a day outside the cover shows its nearest day, a text that is not a day shows the form's starting day (as legacy).
(function () {
  const card = document.getElementById('cancel-preview');
  if (!card) return;
  const previews = JSON.parse(card.dataset.previews);
  const parts = [...card.querySelectorAll('[data-p]')];
  document.addEventListener('date-change', (event) => {
    const box = event.target;
    if (!box.querySelector || !box.querySelector('input[name=cancel_on]')) return;
    const days = Object.keys(previews);
    let day = /^\d{4}-\d{2}-\d{2}$/.test(event.detail) ? event.detail : card.dataset.start;
    if (day < days[0]) day = days[0];
    if (day > days[days.length - 1]) day = days[days.length - 1];
    const p = previews[day];
    if (!p) return;
    for (const el of parts) {
      if (el.dataset.p === 'label') el.textContent = p[6] === '1' ? card.dataset.return : card.dataset.owed;
      else el.textContent = p[Number(el.dataset.p)];
    }
  });
})();
