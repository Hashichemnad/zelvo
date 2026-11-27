"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { PeopleList } from "@/components/catalog/PeopleList";
import { BilingualInputs, Localized } from "@/components/directory/common";
import { buttonClass, inputClass, secondaryButtonClass } from "@/components/incidents/ReportIncidentForm";
import { Link, useRouter } from "@/i18n/navigation";
import { deleteRequest, postJson, putJson } from "@/lib/client-api";
import {
  cabDecisions, fromLocalInput, voteChoices,
  type AgendaItem, type CabDecision, type ChangeBoard, type ChangeBoardEntry, type Meeting, type VoteChoice,
} from "@/lib/changes";
import { formatDateTime, type DateDisplayOptions } from "@/lib/format";
import type { Locale } from "@/lib/incidents";
import { ChangeStageBadge, ChangeStatusLine, RiskBadge, useSaver } from "./common";

/** Plans a meeting of the change advisory board (the change manager or the board's chair); the agenda is built by Zelvo. */
export function PlanMeetingForm({ csrfToken }: { csrfToken: string }) {
  const t = useTranslations("changes.board.plan");
  const router = useRouter();
  const id = useId();
  const { status, run, busy } = useSaver();
  const [en, setEn] = useState("");
  const [ar, setAr] = useState("");
  const [start, setStart] = useState("");
  const [end, setEnd] = useState("");
  const [location, setLocation] = useState("");
  return (
    <form className="space-y-3 rounded-lg border border-line p-4" aria-labelledby={`${id}-heading`} onSubmit={(e) => {
      e.preventDefault();
      void run(async () => {
        const meeting = await postJson<Meeting>("/v1/cab-meetings",
          { title: { en: en || null, ar: ar || null }, startsAt: fromLocalInput(start), endsAt: fromLocalInput(end), location: location || null }, csrfToken);
        router.push(`/changes/board/${meeting.id}`);
      });
    }}>
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h2>
      <p className="text-sm text-ink-muted">{t("hint")}</p>
      <BilingualInputs id={`${id}-title`} label={t("title")} en={en} ar={ar} maxLength={200} onChange={(v) => (setEn(v.en), setAr(v.ar))} />
      <div className="grid gap-3 sm:grid-cols-3">
        <label className="space-y-1 text-sm"><span className="block">{t("start")}</span>
          <input type="datetime-local" required value={start} onChange={(e) => setStart(e.target.value)} className={inputClass} dir="ltr" /></label>
        <label className="space-y-1 text-sm"><span className="block">{t("end")}</span>
          <input type="datetime-local" required value={end} onChange={(e) => setEnd(e.target.value)} className={inputClass} dir="ltr" /></label>
        <label className="space-y-1 text-sm"><span className="block">{t("location")}</span>
          <input value={location} onChange={(e) => setLocation(e.target.value)} maxLength={500} dir="auto" className={inputClass} /></label>
      </div>
      <button type="submit" className={buttonClass} disabled={busy || (!en.trim() && !ar.trim()) || !start || !end}>{t("submit")}</button>
      <ChangeStatusLine status={status} />
    </form>
  );
}

function Tally({ item }: { item: AgendaItem }) {
  const t = useTranslations("changes.board.votes");
  return <span className="text-sm">{voteChoices.map((c) => t(`tally.${c}`, { count: item.tally[c] ?? 0 })).join(" · ")}</span>;
}

/** One change on the agenda: its facts, the members' votes (advice), the member's own vote, and the chair's decision with the minutes. */
function AgendaItemCard({ meeting, item, csrfToken, display, onSaved }: {
  meeting: Meeting;
  item: AgendaItem;
  csrfToken: string;
  display: DateDisplayOptions;
  onSaved: (m: Meeting) => void;
}) {
  const t = useTranslations("changes.board");
  const tVotes = useTranslations("changes.board.votes");
  const tKinds = useTranslations("changes.kinds");
  const id = useId();
  const { status, run, busy } = useSaver();
  const [choice, setChoice] = useState<VoteChoice>("Approve");
  const [comment, setComment] = useState("");
  const [decision, setDecision] = useState<CabDecision>("Approved");
  const [decisionComment, setDecisionComment] = useState("");
  const [minutes, setMinutes] = useState("");
  const open = meeting.state === "Planned" && item.decision === null;
  const base = `/v1/cab-meetings/${meeting.id}/agenda/${item.id}`;

  return (
    <li className="space-y-3 rounded-lg border border-line p-4" data-testid="agenda-item">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="font-semibold">
          <Link href={`/changes/${item.changeId}`} className="font-mono text-brand underline" dir="ltr">{item.changeNumber}</Link>{" "}
          <span dir="auto">{item.changeTitle}</span>
        </h3>
        <span className="flex flex-wrap items-center gap-2"><ChangeStageBadge stage={item.stage} /><RiskBadge level={item.riskLevel} /></span>
      </div>
      <p className="text-sm">
        {tKinds(item.kind)}
        {item.plannedStart && item.plannedEnd && <> · {formatDateTime(new Date(item.plannedStart), display)} – {formatDateTime(new Date(item.plannedEnd), display)}</>}
        {" · "}{t("conflicts", { count: item.conflictCount })}
      </p>
      <div className="space-y-1">
        <p className="font-semibold">{tVotes("heading")} <Tally item={item} /></p>
        {item.votes.length === 0 ? <p className="text-sm text-ink-muted">{tVotes("none")}</p> : (
          <ul className="space-y-1 text-sm">
            {item.votes.map((v) => (
              <li key={v.member.id}><span className="font-semibold">{v.member.displayName ?? "—"}</span>: {tVotes(`choices.${v.choice}`)}
                {v.comment && <> — <span dir="auto">{v.comment}</span></>}</li>
            ))}
          </ul>
        )}
      </div>
      {item.decision ? (
        <div className="space-y-1 rounded-md bg-surface-muted p-2">
          <p className="font-semibold">{t(`decisions.${item.decision}`)}{item.decidedBy && <> · {item.decidedBy.displayName}</>}</p>
          {item.comment && <p className="whitespace-pre-wrap" dir="auto">{item.comment}</p>}
          {item.minutes && <p className="whitespace-pre-wrap text-sm" dir="auto"><span className="font-semibold">{t("itemMinutes")}:</span> {item.minutes}</p>}
        </div>
      ) : !item.waiting && meeting.state === "Planned" ? (
        <p className="text-sm text-ink-muted">{t("notWaiting")}</p>
      ) : null}
      {open && meeting.isMember && (
        <fieldset className="space-y-2" data-testid="vote-form">
          <legend className="font-semibold">{tVotes("yours")}</legend>
          <div className="flex flex-wrap gap-4">
            {voteChoices.map((c) => (
              <label key={c} className="flex items-center gap-2">
                <input type="radio" name={`${id}-vote`} className="size-5 accent-brand" checked={choice === c} onChange={() => setChoice(c)} />
                {tVotes(`choices.${c}`)}
              </label>
            ))}
          </div>
          <label className="block space-y-1 text-sm"><span className="block">{tVotes("comment")}</span>
            <textarea value={comment} onChange={(e) => setComment(e.target.value)} rows={2} maxLength={2000} dir="auto" className={inputClass} /></label>
          <button type="button" className={secondaryButtonClass} disabled={busy}
            onClick={() => void run(async () => onSaved(await putJson<Meeting>(`${base}/vote`, { choice, comment: comment || null }, csrfToken)))}>
            {tVotes("submit")}</button>
        </fieldset>
      )}
      {open && meeting.isChair && item.waiting && (
        <fieldset className="space-y-2" data-testid="decision-form">
          <legend className="font-semibold">{t("decide")}</legend>
          <p className="text-sm text-ink-muted">{t("decideHint")}</p>
          <div className="flex flex-wrap gap-4">
            {cabDecisions.map((d) => (
              <label key={d} className="flex items-center gap-2">
                <input type="radio" name={`${id}-decision`} className="size-5 accent-brand" checked={decision === d} onChange={() => setDecision(d)} />
                {t(`decisionChoices.${d}`)}
              </label>
            ))}
          </div>
          <label className="block space-y-1 text-sm"><span className="block">{decision === "Rejected" ? t("reason") : t("decisionComment")}</span>
            <textarea value={decisionComment} onChange={(e) => setDecisionComment(e.target.value)} rows={2} maxLength={2000} dir="auto" className={inputClass} /></label>
          <label className="block space-y-1 text-sm"><span className="block">{t("itemMinutes")}</span>
            <textarea value={minutes} onChange={(e) => setMinutes(e.target.value)} rows={3} maxLength={4000} dir="auto" className={inputClass} /></label>
          <button type="button" className={buttonClass} disabled={busy || (decision === "Rejected" && !decisionComment.trim())}
            onClick={() => void run(async () => onSaved(await postJson<Meeting>(`${base}/decision`,
              { decision, comment: decisionComment || null, minutes: minutes || null }, csrfToken)))}>
            {t("record")}</button>
        </fieldset>
      )}
      {open && meeting.mayManage && (
        <button type="button" className="text-sm font-semibold text-brand underline" disabled={busy}
          onClick={() => void run(async () => {
            await deleteRequest(`${base}`, csrfToken);
            window.location.reload();
          })}>{t("removeItem", { number: item.changeNumber })}</button>
      )}
      <ChangeStatusLine status={status} />
    </li>
  );
}

/** A meeting of the change advisory board: its agenda with votes and decisions, the minutes, and the chair's and change manager's actions. */
export function MeetingView({ initial, csrfToken, display }: { initial: Meeting; csrfToken: string; display: DateDisplayOptions }) {
  const t = useTranslations("changes.board");
  const locale = useLocale() as Locale;
  const id = useId();
  const { status, run, busy } = useSaver();
  const [meeting, setMeeting] = useState(initial);
  const [minutes, setMinutes] = useState(initial.minutes ?? "");
  const planned = meeting.state === "Planned";

  return (
    <div className="space-y-6">
      <dl className="grid gap-3 rounded-lg border border-line p-4 sm:grid-cols-2" data-testid="meeting-facts">
        <div><dt className="font-semibold">{t("when")}</dt><dd>{formatDateTime(new Date(meeting.startsAt), display)} – {formatDateTime(new Date(meeting.endsAt), display)}</dd></div>
        <div><dt className="font-semibold">{t("where")}</dt><dd dir="auto">{meeting.location ?? "—"}</dd></div>
        <div><dt className="font-semibold">{t("chair")}</dt><dd>{meeting.chair.displayName ?? "—"}</dd></div>
        <div><dt className="font-semibold">{t("state")}</dt><dd>{t(`states.${meeting.state}`)}</dd></div>
        <div className="sm:col-span-2"><dt className="font-semibold">{t("members")}</dt><dd>{meeting.members.map((m) => m.displayName ?? "—").join(", ") || "—"}</dd></div>
      </dl>
      {planned && (meeting.mayManage || meeting.isChair) && (
        <div role="group" aria-label={t("actions")} className="flex flex-wrap gap-2">
          {meeting.mayManage && (
            <button type="button" className={secondaryButtonClass} disabled={busy}
              onClick={() => void run(async () => setMeeting(await postJson<Meeting>(`/v1/cab-meetings/${meeting.id}/agenda/refresh`, {}, csrfToken)))}>{t("refresh")}</button>
          )}
          {meeting.isChair && (
            <button type="button" className={secondaryButtonClass} disabled={busy}
              onClick={() => void run(async () => setMeeting(await postJson<Meeting>(`/v1/cab-meetings/${meeting.id}/complete`, {}, csrfToken)))}>{t("complete")}</button>
          )}
          {meeting.mayManage && (
            <button type="button" className={secondaryButtonClass} disabled={busy}
              onClick={() => void run(async () => setMeeting(await postJson<Meeting>(`/v1/cab-meetings/${meeting.id}/cancel`, {}, csrfToken)))}>{t("cancel")}</button>
          )}
        </div>
      )}
      <section aria-labelledby={`${id}-agenda`} className="space-y-3">
        <h2 id={`${id}-agenda`} className="text-xl font-semibold">{t("agenda")}</h2>
        {meeting.agenda.length === 0 ? <p>{t("emptyAgenda")}</p> : (
          <ol className="space-y-3">
            {meeting.agenda.map((item) => (
              <AgendaItemCard key={`${item.id}-${item.decision ?? ""}-${item.votes.length}`} meeting={meeting} item={item} csrfToken={csrfToken} display={display} onSaved={setMeeting} />
            ))}
          </ol>
        )}
      </section>
      <section aria-labelledby={`${id}-minutes`} className="space-y-2 rounded-lg border border-line p-4">
        <h2 id={`${id}-minutes`} className="text-xl font-semibold">{t("minutes")}</h2>
        {(meeting.isChair || meeting.mayManage) && meeting.state !== "Cancelled" ? (
          <>
            <label htmlFor={`${id}-minutes-text`} className="sr-only">{t("minutes")}</label>
            <textarea id={`${id}-minutes-text`} value={minutes} onChange={(e) => setMinutes(e.target.value)} rows={5} maxLength={20000} dir="auto" className={inputClass} />
            <button type="button" className={secondaryButtonClass} disabled={busy}
              onClick={() => void run(async () => setMeeting(await putJson<Meeting>(`/v1/cab-meetings/${meeting.id}/minutes`, { minutes }, csrfToken)))}>{t("saveMinutes")}</button>
          </>
        ) : meeting.minutes ? <p className="whitespace-pre-wrap" dir="auto">{meeting.minutes}</p> : <p>{t("noMinutes")}</p>}
      </section>
      <p className="text-sm"><Link href="/changes/board" className="font-semibold text-brand underline">{t("allMeetings")}</Link> · <Localized text={meeting.title} locale={locale} /></p>
      <ChangeStatusLine status={status} />
    </div>
  );
}

/** A change's time before the board: each meeting with the votes, the chair's decision and the minutes (task H4). */
export function ChangeBoardHistory({ entries, display }: { entries: ChangeBoardEntry[]; display: DateDisplayOptions }) {
  const t = useTranslations("changes.board");
  const tVotes = useTranslations("changes.board.votes");
  const locale = useLocale() as Locale;
  const id = useId();
  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-3 rounded-lg border border-line p-4" data-testid="change-board-history">
      <h2 id={`${id}-heading`} className="text-lg font-semibold">{t("history")}</h2>
      {entries.length === 0 ? <p>{t("noHistory")}</p> : (
        <ol className="space-y-3">
          {entries.map((e) => (
            <li key={e.item.id} className="space-y-1">
              <p>
                <Link href={`/changes/board/${e.meetingId}`} className="font-semibold text-brand underline"><Localized text={e.meetingTitle} locale={locale} /></Link>{" "}
                <span className="text-sm text-ink-muted">{formatDateTime(new Date(e.meetingStartsAt), display)} · {t(`states.${e.meetingState}`)}</span>
              </p>
              <p className="text-sm">{tVotes("heading")} <Tally item={e.item} /></p>
              {e.item.votes.filter((v) => v.comment).map((v) => (
                <p key={v.member.id} className="text-sm"><span className="font-semibold">{v.member.displayName}</span> ({tVotes(`choices.${v.choice}`)}): <span dir="auto">{v.comment}</span></p>
              ))}
              {e.item.decision && <p className="font-semibold">{t(`decisions.${e.item.decision}`)}</p>}
              {e.item.comment && <p className="whitespace-pre-wrap" dir="auto">{e.item.comment}</p>}
              {e.item.minutes && <p className="whitespace-pre-wrap text-sm" dir="auto"><span className="font-semibold">{t("itemMinutes")}:</span> {e.item.minutes}</p>}
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}

/** Settings → Changes → Boards: the advisory board's chair, deputy and members, and the emergency board's members and hours. */
export function BoardsPanel({ initial, csrfToken }: { initial: ChangeBoard[]; csrfToken: string }) {
  const t = useTranslations("changes.settings.boards");
  const id = useId();
  const { status, run, busy } = useSaver();
  const [boards, setBoards] = useState(initial);
  const set = (kind: string, patch: Partial<ChangeBoard>) => setBoards((list) => list.map((b) => (b.kind === kind ? { ...b, ...patch } : b)));
  const person = (pid: string | undefined) => (pid ? { id: pid, displayName: null } : null);

  return (
    <section aria-labelledby={`${id}-heading`} className="space-y-4 rounded-lg border border-line p-4" data-testid="boards">
      <h3 id={`${id}-heading`} className="text-lg font-semibold">{t("heading")}</h3>
      <p className="text-sm text-ink-muted">{t("hint")}</p>
      {boards.map((b) => (
        <form key={b.kind} className="space-y-3 rounded-md border border-line p-3" onSubmit={(e) => {
          e.preventDefault();
          void run(async () => {
            const saved = await putJson<ChangeBoard>(`/v1/change-boards/${b.kind.toLowerCase()}`, {
              name: { en: b.name.en || null, ar: b.name.ar || null }, chairId: b.chair?.id ?? null, deputyChairId: b.deputyChair?.id ?? null,
              memberIds: b.members.map((m) => m.id), decideWithinHours: b.decideWithinHours,
            }, csrfToken);
            set(b.kind, saved);
          });
        }}>
          <h4 className="font-semibold">{t(`kinds.${b.kind}`)}</h4>
          <BilingualInputs id={`${id}-${b.kind}-name`} label={t("name")} en={b.name.en ?? ""} ar={b.name.ar ?? ""} maxLength={200}
            onChange={(v) => set(b.kind, { name: { en: v.en || null, ar: v.ar || null } })} />
          {b.kind === "Advisory" ? (
            <div className="grid gap-3 sm:grid-cols-2">
              <PeopleList legend={t("chair")} single ids={b.chair ? [b.chair.id] : []} idPrefix={`${id}-chair`} onChange={(ids) => set(b.kind, { chair: person(ids[0]) })} />
              <PeopleList legend={t("deputy")} single ids={b.deputyChair ? [b.deputyChair.id] : []} idPrefix={`${id}-deputy`} onChange={(ids) => set(b.kind, { deputyChair: person(ids[0]) })} />
            </div>
          ) : (
            <label className="block space-y-1 text-sm"><span className="block">{t("hours")}</span>
              <input type="number" min={1} max={168} value={b.decideWithinHours} onChange={(e) => set(b.kind, { decideWithinHours: Number(e.target.value) })} className={`${inputClass} w-28`} dir="ltr" /></label>
          )}
          <PeopleList legend={t("members")} ids={b.members.map((m) => m.id)} idPrefix={`${id}-${b.kind}-members`}
            onChange={(ids) => set(b.kind, { members: ids.map((m) => ({ id: m, displayName: null })) })} />
          <button type="submit" className={buttonClass} disabled={busy}>{t("save", { name: t(`kinds.${b.kind}`) })}</button>
        </form>
      ))}
      <ChangeStatusLine status={status} />
    </section>
  );
}
