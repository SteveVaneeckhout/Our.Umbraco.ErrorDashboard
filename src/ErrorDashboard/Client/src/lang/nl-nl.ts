/**
 * Language Alias: nl-nl
 * Language Int Name: Dutch (NL)
 * Language Local Name: Nederlands (NL)
 * Language Culture: nl-NL
 *
 * Typed as `Translation<ErrorDashboardLocalizations>`, so leaving a key out of this file is a build
 * error rather than a silent fall back to English.
 */
import type { UmbLocalizationDictionary } from "@umbraco-cms/backoffice/localization-api";
import type { ErrorDashboardLocalizations } from "./en.js";
import type { Translation } from "./types.js";

const nl: Translation<ErrorDashboardLocalizations> = {
  errorDashboardShared: {
    coverageNote:
      "Gerapporteerd door de browsers van bezoekers via Network Error Logging, wat alleen browsers op " +
      "basis van Chromium ondersteunen. Firefox, Safari en verkeer dat niet van een browser komt " +
      "tellen niet mee, dus de werkelijke aantallen liggen hoger.",
    aboutTheseNumbers: "Over deze cijfers",

    period: "Periode",
    lastDays: (days: number) => `Afgelopen ${days} dagen`,
    host: "Host",
    filterByHost: "Filter op host",
    allHosts: "Alle hosts",

    noDataForPeriod: "Geen gegevens voor deze periode.",
    chartAriaLabel: "Fouten per dag over de geselecteerde periode",
    chartTooltip: (day: string, count: number) =>
      count === 1 ? `${day}: 1 fout` : `${day}: ${count} fouten`,
    chartPeak: (peak: string) => `piek ${peak}/dag`,

    errorTypeDns: "DNS",
    errorTypeConnection: "Verbinding",
    errorTypeCertificate: "Certificaat",
    errorTypeHttpError: "HTTP-fout",
    errorTypeHttpProtocol: "HTTP-protocol",
  },

  errorDashboardOverview: {
    label: "Fouten",
    headline: "Fouten gemeld door bezoekers",

    emailMeAboutSpikes: "Mail mij bij pieken in fouten",
    subscribedNoEmailHeadline: "Aangemeld, maar e-mail is niet geconfigureerd",
    subscribedNoEmailMessage:
      "Umbraco heeft geen SMTP-configuratie, dus waarschuwingsmails kunnen niet worden bezorgd. " +
      "Stel Umbraco:CMS:Global:Smtp in (een From-adres plus een Host of een PickupDirectoryLocation).",
    alertsOnHeadline: "Waarschuwingen aan",
    alertsOnMessage: "Je ontvangt een e-mail wanneer het aantal fouten op deze site afwijkend lijkt.",
    alertsOffHeadline: "Waarschuwingen uit",
    alertsOffMessage: "Je ontvangt geen foutwaarschuwingen meer.",

    statLast24Hours: "Afgelopen 24 uur",
    statErrorRate: "Geschat foutpercentage",
    statErrorRateHint: (requests: string) => `van ~${requests} bemonsterde verzoeken`,
    statNotYetCounted: "Nog niet meegeteld",
    statNotYetCountedHint: "wordt elk uur samengevoegd",

    isThisUnusual: "Is dit ongewoon?",
    noAssessment: "Geen beoordeling beschikbaar.",
    aboveNormal: "Boven normaal",
    normal: "Normaal",
    howJudged: (baselineDays: number) =>
      `Beoordeeld tegen de ${baselineDays} dagen vóór vandaag, op basis van de mediaan en de ` +
      "absolute afwijking daarvan - zo past de drempel zich aan hoe onrustig juist deze site " +
      "normaal is, in plaats van aan een vast aantal fouten.",

    explanationInsufficientHistory: (days: string, required: string) =>
      `Slechts ${days} dagen historie; er zijn er ${required} nodig voordat er iets wordt beoordeeld.`,
    explanationBelowFloor: (observed: string, floor: string) =>
      `${observed} fouten ligt onder de ondergrens van ${floor}.`,
    explanationBelowRatio: (observed: string, ratio: string, median: string) =>
      `${observed} is minder dan ${ratio}x de gebruikelijke ${median}.`,
    explanationWithinSpread: (score: string, threshold: string) =>
      `Score ${score} valt binnen de normale spreiding van deze site (drempel ${threshold}).`,
    explanationAnomalous: (observed: string, median: string, score: string) =>
      `${observed} fouten tegenover een gebruikelijke ${median} - ${score} keer de normale variatie ` +
      "van deze site.",

    whatIsFailing: "Wat er misgaat",
    nothingReported: "Niets gemeld in deze periode.",

    notYetAggregated: "Nog niet samengevoegd",
    updatedAgo: (when: string) => `Bijgewerkt ${when}`,
  },

  errorDashboardPages: {
    label: "Foutpagina's",
    headline: "URL's waarop bezoekers fouten tegenkomen",
    description: "Ergste eerst",

    columnPath: "Pad",
    columnProblem: "Probleem",
    columnErrors: "Fouten",
    columnLastSeen: "Laatst gezien",

    status: "Status",
    filterByStatus: "Filter op statuscode",
    anyStatus: "Alle",
    totalUrls: (count: number) => (count === 1 ? "1 URL" : `${count} URL's`),
    empty: "Geen fouten gemeld voor deze periode.",

    editTitle: (url: string) => `${url} bewerken`,
    showLinksAria: (path: string) => `Toon wat naar ${path} verwijst`,
    hideLinksAria: (path: string) => `Verberg wat naar ${path} verwijst`,
    showLinksTitle: "Toon wat hiernaar verwijst",
    hideLinksTitle: "Verberg wat hiernaar verwijst",

    unpublished: "Niet gepubliceerd",
    unpublishedTitle: "De pagina bestaat, maar is niet gepubliceerd",

    linkingTo: (path: string) => `Verwijst naar <span class="path">${path}</span>`,
    noReferrers: (days: number) => `Geen verwijzers vastgelegd in de afgelopen ${days} dagen.`,
    referrerWindowNote: (retentionDays: number, rangeDays: number) =>
      `Verwijzers komen uit ruwe meldingen, die ${retentionDays} dagen worden bewaard — korter dan ` +
      `de periode van ${rangeDays} dagen hierboven.`,
    referrerCount: (count: string) => `${count}x`,
    noReferrer: "Geen verwijzer — rechtstreeks bezocht, een bladwijzer, of weggelaten door een referrerbeleid",

    otherPaths: "(overige paden)",

    missingAssetsNote:
      "Ontbrekende bestanden - oude afbeeldingspaden, een favicon, een verouderd script - vormen " +
      "hier meestal het grootste deel van het volume, niet pagina's. Sorteer op pad om ze snel te " +
      "herkennen.",
  },

  errorDashboardAlerts: {
    label: "Foutwaarschuwingen",

    alertHistory: "Waarschuwingsgeschiedenis",
    recomputeNow: "Nu opnieuw berekenen",
    recomputing: "Bezig met berekenen…",
    recomputedHeadline: "Opnieuw berekend",
    recomputedWithAlert: (aggregated: number, pruned: number) =>
      `${aggregated} melding(en) samengevoegd, ${pruned} rij(en) opgeruimd en een waarschuwing gegeven.`,
    recomputedNoAlert: (aggregated: number, pruned: number) =>
      `${aggregated} melding(en) samengevoegd en ${pruned} rij(en) opgeruimd. Geen nieuwe waarschuwing gegeven.`,

    noAlerts: "Er zijn geen waarschuwingen gegeven. Niets zag er afwijkend uit.",
    alertBody: (observed: string, median: string, score: string) =>
      `<span class="count">${observed}</span> fouten in 24 uur tegenover een gebruikelijke ` +
      `<span class="count">${median}</span> per dag ` +
      `(<span class="count">${score}</span>x de normale variatie van deze site).`,
    alertEmailed: (count: number) =>
      count === 1 ? "Gemaild naar 1 abonnee." : `Gemaild naar ${count} abonnees.`,

    subscribers: "Abonnees",
    noSubscribers: "Niemand is aangemeld. Gebruikers melden zich aan met de schakelaar op het tabblad Fouten.",
    lastEmailed: "laatst gemaild",
    neverEmailed: "nooit gemaild",

    settings: "Instellingen",
    emailNotConfigured: "E-mail is niet geconfigureerd",
    emailNotConfiguredNote:
      "Umbraco heeft geen SMTP-instellingen, dus waarschuwingen zouden stilzwijgend verdwijnen. Stel " +
      "<code>Umbraco:CMS:Global:Smtp</code> in met een <code>From</code>-adres en een " +
      "<code>Host</code> of een <code>PickupDirectoryLocation</code>.",

    settingAlerting: "Waarschuwen",
    settingCollectorPath: "Verzamelpad",
    settingBaseline: "Referentieperiode",
    settingTriggerScore: "Drempelscore",
    settingMinimumErrors: "Minimum aantal fouten",
    settingMinimumMultiple: "Minimale factor",
    settingCooldown: "Afkoelperiode",
    settingRawRetention: "Bewaartermijn ruwe data",
    settingRateLimit: "Snelheidslimiet verzamelaar",
    settingAcceptedHosts: "Geaccepteerde hosts",
    settingSuccessSampling: "Steekproef geslaagde verzoeken",

    on: "Aan",
    off: "Uit",
    baselineValue: (days: number, minimum: number) => `${days} dagen (min. ${minimum})`,
    minimumMultipleValue: (ratio: string) => `${ratio}x het gebruikelijke`,
    cooldownValue: (hours: number) => `${hours} uur`,
    rawRetentionValue: (days: number) => `${days} dagen`,
    rateLimitValue: (perMinute: number) => `${perMinute} payloads/min per IP`,
    requestHostOnly: "Alleen de host van het verzoek",
    successSamplingValue: (percentage: string) => `${percentage}% van de geslaagde verzoeken`,
    successSamplingOff: "Uit - geen noemer voor het verkeer",

    thresholdNote:
      "Een waarschuwing wordt gegeven wanneer de afgelopen 24 uur alle bovenstaande drempels " +
      "tegelijk overschrijden. De score is de afstand tot de mediaan van de referentiedagen, " +
      "gemeten in mediane absolute afwijkingen, dus een site met van nature veel fouten heeft een " +
      "grotere sprong nodig dan een rustige site.",
    noDomainsNote:
      "Er zijn geen Umbraco-domeinen geconfigureerd, dus de verzamelaar kan een melding alleen " +
      "vergelijken met de <code>Host</code>-header van het verzoek zelf — die door de aanroeper " +
      "wordt meegegeven. Configureer een domein in Umbraco, of geef hostnamen op onder " +
      "<code>ErrorDashboard:AllowedHosts</code>, om dat gat te dichten.",
  },
};

export default nl satisfies UmbLocalizationDictionary;
