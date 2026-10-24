// A date field shown and typed as dd/mm/yyyy whatever the browser's language (legacy src/app/date-input.tsx and
// src/lib/date-text.ts). The form still sends the ISO date under the field's name, from a hidden input, so the server
// code reads yyyy-mm-dd. The calendar button opens the browser's own date picker. Markup: Infrastructure/DateInputTagHelper.cs.
(function () {
  'use strict';
  var ISO = /^(\d{4})-(\d{2})-(\d{2})$/;
  var SIZES = [2, 2, 4];

  // 2026-10-14 → 14/10/2026; anything that is not an ISO date → ''.
  function isoToText(iso) {
    var m = ISO.exec(iso || '');
    return m ? m[3] + '/' + m[2] + '/' + m[1] : '';
  }

  // 14/10/2026 (also 14.10.2026, 14-10-2026 and one-digit day or month) → 2026-10-14; not a real calendar day → null.
  function textToIso(text) {
    var m = /^(\d{1,2})[/.-](\d{1,2})[/.-](\d{4})$/.exec(text.trim());
    if (!m) return null;
    var day = Number(m[1]), month = Number(m[2]), year = Number(m[3]);
    var date = new Date(Date.UTC(year, month - 1, day));
    if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) return null;
    return m[3] + '-' + String(month).padStart(2, '0') + '-' + String(day).padStart(2, '0');
  }

  // What the field shows while typing: the digits fill day, month and year in turn (two, two, four); a separator the
  // person types ends the day or month early and shows as a slash; other characters are dropped.
  function typingMask(raw) {
    var parts = raw.replace(/[.-]/g, '/').replace(/[^\d/]/g, '').split('/');
    var fields = ['', '', ''];
    var f = 0;
    parts.forEach(function (part, i) {
      for (var k = 0; k < part.length; k++) {
        while (f < 2 && fields[f].length >= SIZES[f]) f++;
        if (fields[f].length < SIZES[f]) fields[f] += part[k];
      }
      if (i < parts.length - 1 && fields[f] !== '' && f < 2) f++;
    });
    return fields.slice(0, f + 1).join('/');
  }

  // Where the caret goes once the field is regrouped: after as many digits as stood before it in what was typed.
  function caretAfter(shown, typed, caret) {
    var digits = typed.slice(0, caret).replace(/\D/g, '').length;
    var pos = 0;
    for (var seen = 0; pos < shown.length && seen < digits; pos++) if (/\d/.test(shown[pos])) seen++;
    if (caret > 0 && /[/.-]/.test(typed[caret - 1] || '')) while (shown[pos] === '/') pos++;
    return pos;
  }

  function setup(box) {
    if (box.dataset.ready) return;
    box.dataset.ready = '1';
    var visible = box.querySelector('input[type=text]');
    var hidden = box.querySelector('input[type=hidden]');
    var picker = box.querySelector('input[type=date]');
    var button = box.querySelector('button');
    var min = box.dataset.min || '', max = box.dataset.max || '';
    var t = { invalid: box.dataset.invalid, early: box.dataset.early, late: box.dataset.late };

    function apply(next) {
      visible.value = next;
      var parsed = next === '' ? '' : textToIso(next);
      var message = '';
      if (parsed === null) message = t.invalid;
      else if (parsed && min && parsed < min) message = t.early.replace('{0}', isoToText(min));
      else if (parsed && max && parsed > max) message = t.late.replace('{0}', isoToText(max));
      visible.setCustomValidity(message);
      hidden.value = parsed || '';
      picker.value = parsed || '';
      box.dispatchEvent(new CustomEvent('date-change', { bubbles: true, detail: hidden.value }));
    }

    visible.addEventListener('input', function () {
      var typed = visible.value;
      var shown = typingMask(typed);
      var caret = caretAfter(shown, typed, visible.selectionStart == null ? typed.length : visible.selectionStart);
      apply(shown);
      if (document.activeElement === visible) visible.setSelectionRange(caret, caret);
    });
    picker.addEventListener('change', function () { apply(isoToText(picker.value)); });
    button.addEventListener('click', function () {
      try { picker.showPicker(); } catch (e) { visible.focus(); }
    });
  }

  function all() { document.querySelectorAll('.date-input').forEach(setup); }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', all); else all();
  window.dateInput = { setup: all, isoToText: isoToText, textToIso: textToIso, typingMask: typingMask, caretAfter: caretAfter };
})();
