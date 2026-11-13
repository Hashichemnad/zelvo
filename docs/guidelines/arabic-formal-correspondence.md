# Formal Arabic in templates (GOV-8, base)

These guidelines apply to every Arabic text that Zelvo sends or prints on behalf of an entity: email and notification templates (task B5), approval messages (C3), receipts (E8) and PDF exports (F1). Government entities in the UAE expect the tone of official correspondence. The guidelines are the base for GOV-8. The entity letterhead itself comes with branding in F1.

Whoever writes or reviews an Arabic template (Zelvo staff, a partner or a tenant administrator) works through the checklist at the end.

## Language and register

- **Modern Standard Arabic (فصحى) only.** Do not use Gulf dialect or colloquial words: write "يُرجى" and never "لو سمحت", and write "حاليًا" and never "الحين".
- **No transliteration and no Arabizi.** Product and entity names appear in Arabic: "زيلفو" (approved), and the entity's Arabic name from its branding settings.
- **Written by a person, then reviewed.** A machine translation of the English template is only a first draft. Every Arabic template is reviewed by a fluent Arabic writer before release.
- **Each language is its own text.** The Arabic and English templates say the same things. The Arabic is written for an Arabic reader and does not follow the English word order.

## Addressing the reader

- **Plural of respect in official letters:** "نحيطكم علمًا"، "يسرّنا إفادتكم"، "نرجو منكم". Short notifications (status changes, reminders) may use impersonal forms instead: "تم استلام طلبكم".
- **Customers are "المتعامل".** Write "المتعامل الكريم" or "المتعاملون الكرام", the term UAE government service charters use. Do not write "العميل" in government-facing templates.
- **With a name:** "السيد/ ..." and "السيدة/ ..." when the template knows the title. Otherwise use the name alone after the greeting, such as "تحية طيبة، فاطمة الشامسي". Do not guess a gender.
- **Neutral wording before slashed forms.** Prefer "يُرجى المراجعة" to "راجع/ي". Use a slashed form only when no neutral wording exists.
- **Requests are polite, not commands:** "يُرجى تحديث البيانات" and "نأمل التكرم بإرفاق المستند", not "حدّث البيانات".

## Structure of a message

1. **Letterhead.** Entity logo and name, added by the template engine from branding (F1).
2. **Greeting.** "السلام عليكم ورحمة الله وبركاته،" or "تحية طيبة وبعد،". Use one greeting per message.
3. **Subject line** in letters and email subjects: "الموضوع: ..." in letters. In email subjects, put the reference first, for example "طلب رقم REQ-1042: تم الاعتماد".
4. **Body.** Short paragraphs. Put the most important fact (status, decision, what to do next) in the first sentence.
5. **Next step.** Give one clear action with its link or contact, such as "للاطلاع على تفاصيل الطلب، يُرجى زيارة بوابة الخدمات".
6. **Closing.** In letters: "وتفضلوا بقبول فائق الاحترام والتقدير،". In notifications: "مع خالص التحية،".
7. **Signature.** The entity's Arabic name or department, never a person's private details unless the template asks for them.

## Numbers, dates and mixed text

- **Digits follow the recipient.** A notification goes out in the recipient's language (LANG-6) and uses the recipient's digits setting (LANG-8). The template engine formats numbers and dates; templates never contain pre-formatted values.
- **Dates name their calendar** in letters: "١٥ جمادى الأولى ١٤٤٨هـ" and "١٣ نوفمبر ٢٠٢٦م". Official letters may show both calendars when the entity asks for it (letterhead option, F1).
- **Times use the 12-hour clock** with "ص" and "م", for example "١٠:٣٠ ص".
- **Currency is "درهم"** after the amount, for example "٢٥٠ درهمًا". Do not use "AED" inside Arabic sentences.
- **Latin text inside Arabic** (reference numbers, email addresses, links) is wrapped so it keeps its own direction (`<bdi>` or `dir="ltr"`). This stops it reordering the sentence around it.

## Punctuation and typography

- **Arabic punctuation:** the comma "،", the semicolon "؛", the question mark "؟" and the quotation marks « ».
- **No exclamation marks or emojis** in official correspondence.
- **Few diacritics.** Add them only where a word would otherwise be ambiguous, for example "يُرجى" and "تمّ".
- **Kashida only from the font.** Do not stretch words with tatweel (ـ) to fill lines.

## Terms

| English | Arabic | Status |
| --- | --- | --- |
| Zelvo | زيلفو | Approved 2026-11-13 |
| Powered by Zelvo | بتقنية زيلفو | Approved 2026-11-13 |
| Customer (government) | المتعامل | Proposed |
| Request | طلب | Proposed |
| Complaint / inquiry / suggestion | شكوى / استفسار / اقتراح | Proposed |
| Approval | اعتماد | Proposed |

The product owner approves proposed terms when task B5 builds the first templates. Approved terms then move into this table.

## Review checklist

- [ ] Modern Standard Arabic; no dialect, Arabizi or transliterated names.
- [ ] The reader is addressed respectfully (plural of respect or impersonal forms), with no guessed gender.
- [ ] Greeting, body, a single next step, and the closing that fits the message type.
- [ ] Numbers and dates come from placeholders that the engine formats per recipient; no hard-coded digits.
- [ ] Latin references are wrapped so they keep their direction.
- [ ] Arabic punctuation; no exclamation marks or emojis.
- [ ] Terms match the table above.
- [ ] A fluent Arabic writer reviewed the final text, not only the translation draft.
