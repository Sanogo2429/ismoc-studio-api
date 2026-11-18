using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using System.ClientModel;
using System.Text.Json.Nodes;
using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace IsMocStudio.Business.Services
{
    public class OpenAiMvpService : IOpenAiMvpService
    {
        /// <summary>Modèle de design (génération, mode manuel, retouches, vision). Surchargable via OpenAI:DesignModel.</summary>
        private const string DefaultDesignModel = "gpt-4o";

        /// <summary>Modèle utilitaire (devis, réparation JSON). Surchargable via OpenAI:UtilityModel.</summary>
        private const string DefaultUtilityModel = "gpt-4o-mini";

        /// <summary>
        /// Moteur UI prioritaire (Claude). Modèle actif sans option bêta ; « claude-sonnet-4-20250514 » est retiré
        /// depuis le 15/06/2026 et renverrait une erreur 404.
        /// </summary>
        private const string DefaultAnthropicDesignModel = "claude-sonnet-4-6";

        /// <summary>Modèle Claude utilitaire, utilisé seulement si OpenAI n'est pas configuré.</summary>
        private const string DefaultAnthropicUtilityModel = "claude-haiku-5-5";

        /// <summary>Schéma de sortie structurée d'une génération : « designPlan » rédigé avant « html ».</summary>
        private static JsonObject GenerationSchema() => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["designPlan"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["html"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray("designPlan", "html"),
            ["additionalProperties"] = false
        };

        /// <summary>Schéma de sortie structurée d'une retouche (html / designPlan / préférence peuvent être null).</summary>
        private static JsonObject RefineSchema()
        {
            static JsonObject Nullable(JsonObject schema) => new() { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["reply"] = new JsonObject { ["type"] = "string" },
                    ["designPlan"] = Nullable(new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }),
                    ["html"] = Nullable(new JsonObject { ["type"] = "string" }),
                    ["extractedPreference"] = Nullable(new JsonObject { ["type"] = "string" })
                },
                ["required"] = new JsonArray("reply", "designPlan", "html", "extractedPreference"),
                ["additionalProperties"] = false
            };
        }

        /// <summary>Valeurs par défaut (surchargeables via OpenAI:MaxOutputTokens et OpenAI:Temperature).</summary>
        private const int DefaultMaxOutputTokens = 16384;
        private const float DefaultTemperature = 0.7f;
        private const int MaxHistoryMessages = 30;

        /// <summary>Règles communes à toute génération ou retouche de maquette (niveau Lovable / v0 / Figma).</summary>
        private const string HtmlDesignRules = """
            STANDARD DE QUALITÉ — NIVEAU AGENCE / LOVABLE / V0 / FIGMA (NON NÉGOCIABLE) :
            - INTERDIT : maquette squelette à un seul bloc, page avec un titre et deux <input>, formulaire basique sur fond blanc vide, bandeaux vert/rouge criards (#65a30d, bg-green-600 plein écran, bg-red-600…), lorem ipsum, page vitrine générique. Un document de moins de 250 lignes de HTML est considéré comme un échec : vise 350 à 700 lignes, indentées (une balise par ligne).
            - Palette harmonieuse : un neutre slate/zinc + une couleur de marque saturée mais élégante + un accent ; dégradés subtils, halos flous en arrière-plan, coins généreux (rounded-2xl/3xl), ombres douces (shadow-xl shadow-slate-900/5), bordures fines (border-slate-200/80, ring-1 ring-black/5), espacements aérés (py-20 lg:py-28 pour les sections), micro-interactions (hover:-translate-y-0.5, transition, active:scale-95, focus-visible:ring).
            - Typographie : Google Fonts « Plus Jakarta Sans » (titres) et « Inter » (texte), chargées dans le <head> et déclarées dans tailwind.config (fontFamily.display et fontFamily.sans). Titres tracking-tight font-extrabold, sur-titres uppercase tracking-widest text-xs, chiffres clés mis en avant.
            - ICÔNES : exclusivement Lucide Icons via <script src="https://unpkg.com/lucide@latest"></script> dans le <head> et des balises <i data-lucide="nom-de-l-icone" class="w-5 h-5"></i> (ex. search, map-pin, car, calendar, star, phone, message-circle, shield-check, credit-card, wifi, battery-full). Appelle lucide.createIcons() à la fin du script ET après chaque rendu dynamique. N'écris JAMAIS de longs chemins <svg><path …/></svg> en dur pour les icônes (seul le logo peut être un petit SVG sur mesure). INTERDICTION ABSOLUE des caractères spéciaux ou emojis utilisés comme icônes (↤, ☞, ⚙, ☰, ✆, ▶…) et des mots « Réseau », « Batterie » ou « Wifi » écrits à la place d'une icône.
            - ÉCONOMIE DE CODE (OBLIGATOIRE) : toutes les données initiales (trajets, produits, annonces, avis, commandes, utilisateurs…) sont déclarées UNE seule fois dans des tableaux JavaScript (const trips = [...]) ; les cartes, listes, lignes et avis répétitifs sont générés par boucle et template littéral (array.map(...).join('')) dans des fonctions render…(). Ne duplique JAMAIS des blocs HTML identiques : un seul modèle de carte par type. Cela réduit fortement la taille du document et rend filtres et ajouts dynamiques fiables.
            - DONNÉES : interface pré-remplie avec des données réalistes, nombreuses et propres au métier (vrais quartiers et rues, prénoms et noms, tarifs, horaires, dates, statuts, notes 4.8★, nombre d'avis). Monnaie cohérente avec le marché ciblé (FCFA, €…).
            - IMAGES : photos cohérentes via https://images.unsplash.com/photo-… (identifiants réels connus) ou https://loremflickr.com/{largeur}/{hauteur}/{mots-cles-anglais}?lock={nombre} ; chaque <img> a un alt et un onerror vers un visuel de secours (data:image/svg+xml). Sinon, avatars à initiales sur fond dégradé.

            STRUCTURE OBLIGATOIRE — SITE WEB / MARKETPLACE / SAAS (dans cet ordre) :
            1. Navbar sticky moderne : <header class="sticky top-0 z-40 backdrop-blur-md bg-white/90 border-b border-slate-200/80"> avec logo SVG sur mesure, liens de navigation, sélecteur de vue si pertinent (ex. Passager / Conducteur, Acheteur / Vendeur) qui change réellement le contenu, et un bouton d'action principal contrasté.
            2. Hero riche en 2 colonnes : badge accrocheur, titre fort (text-4xl lg:text-5xl font-extrabold tracking-tight), sous-titre clair, barre de recherche/réservation interactive intégrée (champs + bouton qui filtre la grille), statistiques de confiance (ex. +2 400 trajets, 4.9/5 avis, 98 % de satisfaction) et, à droite, un visuel ou une carte d'aperçu élégante (mockup, carte SVG, cartes empilées).
            3. Grille interactive de résultats/offres : 6 cartes détaillées minimum, générées depuis un tableau JS de données, avec avatar/initiales, badge vérifié, caractéristiques (modèle, capacité, options), horaires, lieu exact de rendez-vous, tarif mis en valeur, bouton principal (Réserver/Acheter) et bouton de contact direct (WhatsApp / Appel) ; cliquer sur une carte ou « Détails » ouvre une vraie modale de détails. Filtres/tri en puces au-dessus de la grille, mis à jour en direct, avec compteur de résultats et état vide soigné.
            4. Parcours secondaire interactif (ex. « Publier une annonce », « Passer commande », « Prendre rendez-vous ») accessible via un bouton/onglet dédié : formulaire complet en étapes avec validation, puis étape de paiement réaliste (Wave, Orange Money, MTN MoMo, Carte bancaire) avec sélection du moyen, récapitulatif et confirmation ; la validation ajoute réellement l'élément dans la liste/grille et affiche un toast.
            5. Section « Comment ça marche » : 3 étapes numérotées avec icônes SVG fines et liaison visuelle.
            6. Section Sécurité, Confiance & Avis : garanties (paiement sécurisé, profils vérifiés, assistance) et 3 cartes témoignages (avatar, nom, ville, note, citation).
            7. Footer complet multi-colonnes : <footer class="bg-slate-950 text-slate-300"> avec colonnes Navigation, Légal, Contact et réseaux sociaux (icônes SVG), mention de copyright.

            STRUCTURE OBLIGATOIRE — APPLICATION MOBILE (VTC/Uber/Bolt, livraison, réseau social, banque, fitness…) :
            - Le <body> contient un fond en dégradé doux et UN SEUL châssis smartphone, conteneur unique exactement :
              <div id="phone" class="w-[390px] h-[812px] mx-auto my-6 rounded-[44px] border-[10px] border-slate-900 shadow-2xl overflow-hidden flex flex-col justify-between relative bg-white">
            - TOUS les éléments restent STRICTEMENT À L'INTÉRIEUR de ce div : (1) barre de statut (heure 9:41 ; icônes Lucide signal, wifi et battery-full) ; (2) zone de contenu en flex-1 min-h-0 overflow-y-auto (seule zone qui défile) ; (3) bottom sheet / modales en position absolute à l'intérieur du châssis ; (4) barre d'onglets inférieure fonctionnelle (icônes Lucide + libellés, onglet actif coloré) qui navigue entre 3 à 4 écrans COMPLETS et différents (ex. Accueil, Recherche/Activité, Messages, Profil). Rien ne déborde du châssis.
            - Applications VTC / taxi : carte urbaine SVG interactive (quartiers, rues nommées, tracé d'itinéraire coloré, véhicules disponibles), bottom sheet avec adresses modifiables, 3 gammes de véhicules cliquables avec temps d'approche et prix calculé, bouton « Commander » qui lance une recherche animée puis affiche le chauffeur en approche (photo, nom, note, véhicule, plaque, ETA décroissant) avec Annuler / Contacter.

            ARCHITECTURE SPA MULTI-VUES (OBLIGATOIRE) — un prototype cliquable, pas une page statique :
            - Le document contient plusieurs vues commutables instantanément, chacune dans un conteneur <section data-view="…">, au minimum : data-view="home", data-view="explore", data-view="create", data-view="checkout", data-view="account" (adapter les libellés au métier, garder ces identifiants). Une seule vue visible à la fois ; changer de vue fait défiler en haut, met à jour l'état actif du menu (aria-current + style) et l'historique interne.
            - Navigation déclarative : chaque élément de navigation porte data-nav="<vue>" et un gestionnaire délégué unique les relie. Chaque vue secondaire possède un bouton « Retour » fonctionnel (pile d'historique JS).
            - ZÉRO LIEN OU BOUTON MORT : interdiction de href="#" sans action, de boutons décoratifs, de liens vers d'autres pages ou d'URL externes dans la navigation. Utilise des <button type="button"> (ou des <a> avec data-nav) : chaque lien de la navbar, chaque carte et chaque bouton d'action déclenche une vraie transition JavaScript (changement de vue, modale, filtre, toast).
            - Données et formulaires réactifs : les recherches et filtres filtrent réellement la liste affichée (compteur de résultats, état vide) ; cliquer sur une carte ouvre une vraie fiche détaillée ou une modale de réservation/contact remplie avec les données de cet élément ; chaque <form> appelle event.preventDefault(), valide les champs et, à la soumission (ex. publier une annonce puis payer), ajoute réellement l'élément au tableau de données, rafraîchit la liste, revient à la vue concernée et affiche un toast de confirmation.

            JAVASCRIPT — INTERFACE 100 % INTERACTIVE (OBLIGATOIRE) :
            - Un script complet placé juste avant </body> (après tout le HTML), en JavaScript vanilla, qui : gère l'état dans des objets/tableaux JS ; génère les listes/cartes depuis ces données ; filtre et recherche en direct ; navigue entre les vues data-view ; ouvre et ferme modales et tiroirs (bouton fermer, clic sur le fond, touche Échap) ; simule l'ajout d'une annonce/commande et le tunnel de paiement ; affiche des toasts de confirmation. Aucun bouton visible ne doit être inerte et le script ne doit lever aucune erreur (vérifie l'existence des éléments avant de les utiliser).

            ICÔNES : exclusivement Lucide (data-lucide) bien proportionnées (class="w-5 h-5", ou w-4 h-4 dans les petits boutons), alignées avec le texte (inline-flex items-center gap-2). Jamais de formes géométriques brutes en guise d'icône ni de texte mal aligné.

            RÈGLES TECHNIQUES :
            - "html" est TOUJOURS un document complet et autonome commençant par <!DOCTYPE html> : <html lang="fr">, <head> avec <meta charset="UTF-8">, <meta name="viewport" content="width=device-width, initial-scale=1.0">, <title> au nom commercial du projet, <script src="https://cdn.tailwindcss.com"></script>, <script src="https://unpkg.com/lucide@latest"></script>, les <link> Google Fonts (Plus Jakarta Sans + Inter) et un <script> tailwind.config (couleurs de marque, polices).
            - JavaScript vanilla uniquement. Ni React, ni Vue, ni bibliothèque JS, ni appel réseau vers un backend.
            - Textes en français avec accents corrects (é, è, à, ç…).
            """;

        private const string ImageReferenceRules = """
            IMAGE D'INSPIRATION : si une image est jointe (photo, croquis, site ou application qui plaît au client), inspire-toi de son ambiance, de ses couleurs, de sa hiérarchie et de ses composants, puis conçois toi-même une interface interactive complète respectant toutes les règles ci-dessus. Complète les sections et écrans manquants de façon cohérente.
            """;

        /// <summary>Format de sortie commun : le plan de design est rédigé AVANT le code.</summary>
        private const string GenerationOutputFormat = """
            FORMAT DE SORTIE — un objet JSON valide, sans texte ni markdown autour, avec les clés DANS CET ORDRE :
            {"designPlan":["…","…"], "html":"<!DOCTYPE html>…"}
            - "designPlan" : 6 à 8 entrées courtes, rédigées AVANT le code, listant les sections visuelles et les écrans/modales/parcours interactifs que tu vas coder (ex. « Navbar sticky avec sélecteur Passager/Conducteur », « Modale de détails d'un trajet », « Tunnel de paiement Wave / Orange Money »).
            - "html" : le document complet qui implémente INTÉGRALEMENT ce plan.
            """;

        private const string HtmlGenerationPrompt = """
            Tu es IsMoc Studio AI, directeur artistique et développeur front-end senior (niveau Lovable / v0). Crée de zéro une véritable interface interactive sur mesure, adaptée à 100 % à l'idée fournie. Déduis le domaine, les utilisateurs, le marché, les tâches essentielles, les données métier et le style. Un jeu doit être réellement jouable ; un outil métier doit avoir ses écrans de travail ; un commerce ou un service doit avoir son vrai parcours (catalogue, fiche, réservation, paiement, suivi…).
            """ + "\n\n" + GenerationOutputFormat + "\n" + ImageReferenceRules + "\n" + HtmlDesignRules;

        private const string ManualGenerationPrompt = """
            Tu es IsMoc Studio AI, directeur artistique et développeur front-end senior (niveau Lovable / v0). L'utilisateur a modélisé son projet lui-même, étape par étape. Génère de zéro une interface interactive FIDÈLE À 100 % À SES CHOIX EXACTS :
            - format : "web" = site/application web suivant la STRUCTURE OBLIGATOIRE SITE WEB ; "mobile" = application mobile OBLIGATOIREMENT dans le châssis smartphone strictement fermé ;
            - secteur d'activité : contenus, données d'exemple, vocabulaire et visuels propres à ce secteur ;
            - palette : la couleur principale et la couleur secondaire fournies sont les couleurs de marque, déclarées dans tailwind.config (colors.brand et colors.brand2) et utilisées de façon visible mais élégante ;
            - pages/modules : chaque page/module demandé devient une vue ou section navigable et fonctionnelle, dans l'ordre fourni, avec les libellés fournis dans la navigation ;
            - description : respecte toutes les précisions données.
            """ + "\n\n" + GenerationOutputFormat + "\n" + ImageReferenceRules + "\n" + HtmlDesignRules;

        private const string HtmlRefinePrompt = """
            Tu es IsMoc Studio AI, directeur artistique et développeur front-end senior (niveau Lovable / v0) qui fait évoluer la maquette d'un client. Tu reçois la mémoire du projet : l'idée initiale, les préférences persistantes, le document HTML actuel et l'historique de conversation. Respecte toujours les préférences enregistrées.

            RÈGLE ABSOLUE — ZÉRO REFUS, ZÉRO QUESTION :
            - Toute demande de changement, d'amélioration ou d'insatisfaction (« je veux une vraie maquette », « j'aime pas », « change », « fais mieux », « refais », « c'est trop simple », « plus moderne », « refonte »…) exige que tu produises IMMÉDIATEMENT un nouveau document HTML COMPLET dans "html". Il est STRICTEMENT INTERDIT de répondre par une question, de demander des précisions ou de renvoyer "html": null dans ces cas : fais toi-même les meilleurs choix de designer.
            - "html": null est autorisé UNIQUEMENT pour une pure question d'information qui ne demande aucune modification (ex. « quelles technologies conseilles-tu ? »).

            CONSERVATION DE LA RICHESSE (OBLIGATOIRE) :
            - Ne supprime JAMAIS une section, un écran, une modale, un parcours, des données ou une interaction existants, sauf demande explicite. Le document retourné doit être au moins aussi long et aussi interactif que l'actuel, script JavaScript inclus.
            - « Plus moderne », « plus beau », « fais mieux » : garde toute la structure et l'interactivité, et améliore la palette de couleurs (plus harmonieuse), les ombres (shadow-xl shadow-slate-900/5), les espacements (plus aérés), la typographie, les états hover/focus et les micro-interactions (transitions, active:scale-95, apparitions douces).
            - « Je veux une vraie maquette », « c'est trop simple », « refonte » : reconstruis une version beaucoup plus riche respectant la STRUCTURE OBLIGATOIRE, en conservant le domaine du projet et les préférences.
            - Retouche ciblée (couleur, texte, ajout d'une section ou d'un écran…) : applique-la et conserve tout le reste.
            - Si une image est jointe, utilise-la comme référence visuelle de la retouche.

            FORMAT DE SORTIE — JSON strict, clés dans cet ordre :
            {"reply":"réponse naturelle et concise en français résumant les améliorations", "designPlan":["…"] ou null, "html":"document HTML complet ou null", "extractedPreference":"règle courte ou null"}
            - "designPlan" : quand tu modifies la maquette, liste AVANT le code les 6 à 8 sections/écrans/interactions du document final (null sinon).
            - "extractedPreference" : uniquement si l'utilisateur exprime un goût ou une règle durable (ex. « toujours des coins arrondis », « pas de vert ») ; sinon null.
            Aucun markdown ni texte hors JSON.
            """ + "\n\n" + HtmlDesignRules;

        private const string EstimatePrompt = """
            Tu es consultant senior en chiffrage d'applications web et mobiles pour IsMoc Studio (marché français, freelances/agence, TJM 350–600 €). Analyse l'idée initiale, les préférences, l'historique et l'intégralité du HTML actuel (écrans, formulaires, interactions, données manipulées). Estime le périmètre réellement représenté pour en faire un produit de production, pas un site générique.

            Tous les montants sont exclusivement en euros (€), entiers, calculés dynamiquement selon la complexité réelle et les fonctionnalités présentes dans la maquette : n'utilise jamais de montant forfaitaire par défaut.

            Retourne uniquement un JSON strict :
            {"projectName":"nom commercial du projet",
             "projectType":"web|mobile",
             "complexity":"Faible|Moyenne|Élevée",
             "developmentPriceEur":4800,
             "designIdentityPriceEur":1200,
             "monthlyMaintenanceEur":150,
             "estimatedDays":18,
             "screens":["Accueil","Catalogue","Fiche produit"],
             "features":["Recherche et filtres","Panier avec calcul du total"],
             "deliveryTime":{"minDays":10,"maxDays":20,"summary":"ex. 3 à 4 semaines"},
             "breakdown":[
               {"workstream":"Design UI/UX","minDays":2,"maxDays":4,"minPriceEur":800,"maxPriceEur":1600},
               {"workstream":"Développement Frontend","minDays":0,"maxDays":0,"minPriceEur":0,"maxPriceEur":0},
               {"workstream":"Backend / API","minDays":0,"maxDays":0,"minPriceEur":0,"maxPriceEur":0},
               {"workstream":"Base de données","minDays":0,"maxDays":0,"minPriceEur":0,"maxPriceEur":0}
             ],
             "totalBudgetEur":{"min":2200,"max":5300},
             "summary":"Synthèse en une ou deux phrases, en précisant que l'estimation est indicative et à confirmer après cadrage."}

            Les quatre postes Design UI/UX, Développement Frontend, Backend / API et Base de données sont obligatoires (tu peux ajouter Tests & mise en production). Adapte jours et montants à la complexité réelle ; les totaux doivent correspondre à la somme des postes. Jours ouvrés, montants entiers en euros. Pas de markdown.

            Cohérence obligatoire :
            - developmentPriceEur = prix ferme proposé pour le développement sur mesure (frontend, backend/API, base de données, tests), compris dans la fourchette des postes correspondants ;
            - designIdentityPriceEur = identité visuelle complète (logo, charte, design UI/UX finalisé) ;
            - developmentPriceEur + designIdentityPriceEur doit être compris entre totalBudgetEur.min et totalBudgetEur.max ;
            - monthlyMaintenanceEur = maintenance mensuelle (hébergement, mises à jour, correctifs, assistance), proportionnelle à la taille du projet ;
            - estimatedDays = délai réaliste en jours ouvrés, compris entre deliveryTime.minDays et deliveryTime.maxDays ;
            - screens = liste exhaustive des écrans présents dans la maquette ; features = liste des fonctionnalités à développer.
            """;

        private const string JsonRepairPrompt = "Tu es un réparateur strict. Répare la syntaxe JSON pour respecter exactement le schéma indiqué dans le message utilisateur. Préserve le sens et les données (y compris l'intégralité du HTML), ne génère ni markdown ni texte extérieur au JSON.";

        /// <summary>Script injecté de façon déterministe : remplace toute image cassée, même ajoutée dynamiquement.</summary>
        private const string ImageFallbackScript = """
            <script id="ismoc-img-fallback">window.addEventListener('error',function(e){var t=e.target;if(t&&t.tagName==='IMG'&&!t.dataset.ismocFallback){t.dataset.ismocFallback='1';t.src='data:image/svg+xml;utf8,'+encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 300"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#e2e8f0"/><stop offset="1" stop-color="#cbd5e1"/></linearGradient></defs><rect width="400" height="300" fill="url(#g)"/><path d="M150 190l35-45 30 35 20-25 45 55H120z" fill="#94a3b8"/><circle cx="250" cy="115" r="16" fill="#94a3b8"/></svg>');}},true);</script>
            """;

        /// <summary>Google Fonts par défaut, injectées si absentes : Inter (texte) et Plus Jakarta Sans (titres).</summary>
        /// <summary>Longueur minimale d'un document de maquette acceptable.</summary>
        public const int MinimumHtmlLength = 3000;

        /// <summary>
        /// Dessine les icônes Lucide au chargement puis à chaque ajout dans le DOM (rendus dynamiques), avec un délai
        /// de regroupement. createIcons remplace les &lt;i data-lucide&gt; : la boucle s'arrête d'elle-même.
        /// </summary>
        private const string LucideAutoRenderScript = """
            <script id="ismoc-lucide">(function(){var t;function r(){if(window.lucide&&document.querySelector('[data-lucide]:not(svg)')){window.lucide.createIcons();}}document.addEventListener('DOMContentLoaded',r);window.addEventListener('load',r);new MutationObserver(function(){clearTimeout(t);t=setTimeout(r,40);}).observe(document.documentElement,{childList:true,subtree:true});})();</script>
            """;

        /// <summary>Plafond de sortie Claude par défaut (streaming SSE : pas de limite des requêtes non streamées).</summary>
        private const int DefaultAnthropicMaxTokens = 32768;

        /// <summary>Taille minimale d'une maquette tronquée pour être récupérée plutôt que rejetée.</summary>
        public const int MinimumRecoverableHtmlLength = 6000;

        /// <summary>
        /// Récupère le HTML d'une réponse coupée à max_tokens : extrait la valeur (partielle) du champ « html » du JSON,
        /// ou le HTML brut, puis referme proprement le document. Retourne null si le contenu n'est pas exploitable
        /// (pas de &lt;body&gt; ou moins de <see cref="MinimumRecoverableHtmlLength"/> caractères).
        /// </summary>
        public static string? TryRecoverTruncatedHtml(string partialText)
        {
            if (string.IsNullOrWhiteSpace(partialText)) return null;

            var html = ExtractPartialJsonString(partialText, "html", requireClosed: false);
            if (html is null)
            {
                var start = partialText.IndexOf('<');
                html = start >= 0 ? partialText[start..] : null;
            }

            if (html is null || html.Length < MinimumRecoverableHtmlLength || !html.Contains("<body", StringComparison.OrdinalIgnoreCase)) return null;

            var builder = new System.Text.StringBuilder(html);
            var lower = html.ToLowerInvariant();
            var openScripts = Regex.Matches(lower, @"<script\b").Count;
            var closedScripts = Regex.Matches(lower, @"</script\s*>").Count;

            if (openScripts > closedScripts)
            {
                // Coupé au milieu d'un script : on le referme (le JavaScript partiel sera ignoré par le navigateur).
                builder.Append("\n</script>");
            }
            else
            {
                // Coupé au milieu d'une balise HTML (« <div class="… ») : on retire le fragment inachevé.
                var lastOpen = html.LastIndexOf('<');
                if (lastOpen > html.LastIndexOf('>')) builder.Length = lastOpen;
                var lowerOpenStyles = Regex.Matches(lower, @"<style\b").Count;
                if (lowerOpenStyles > Regex.Matches(lower, @"</style\s*>").Count) builder.Append("\n</style>");
            }

            if (!lower.Contains("</body>")) builder.Append("\n</body>");
            if (!lower.Contains("</html>")) builder.Append("\n</html>");
            return builder.ToString();
        }

        /// <summary>
        /// Lit la valeur chaîne d'une clé dans un JSON éventuellement incomplet (décode les échappements).
        /// Avec <paramref name="requireClosed"/>, retourne null si la chaîne n'est pas terminée.
        /// </summary>
        public static string? ExtractPartialJsonString(string json, string key, bool requireClosed)
        {
            var match = Regex.Match(json, $@"""{Regex.Escape(key)}""\s*:\s*""");
            if (!match.Success) return null;

            var builder = new System.Text.StringBuilder();
            for (var index = match.Index + match.Length; index < json.Length; index++)
            {
                var character = json[index];
                if (character == '"') return builder.ToString();
                if (character != '\\')
                {
                    builder.Append(character);
                    continue;
                }

                if (index + 1 >= json.Length) break; // échappement coupé en fin de flux
                var escaped = json[++index];
                switch (escaped)
                {
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'u':
                        if (index + 4 >= json.Length) return requireClosed ? null : builder.ToString();
                        if (int.TryParse(json.AsSpan(index + 1, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var code))
                        {
                            builder.Append((char)code);
                        }
                        index += 4;
                        break;
                    default: builder.Append(escaped); break; // \" \\ \/
                }
            }

            return requireClosed ? null : builder.ToString();
        }

        /// <summary>href="#" / href='#' sans destination → remplacés par href="javascript:void(0)".</summary>
        private static readonly Regex DeadLink = new(@"href\s*=\s*([""'])#\1", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Empêche l'aperçu (iframe srcdoc) de quitter le prototype : soumission de formulaire non interceptée,
        /// lien vers une autre page ou « # » vide. Les liens target="_blank" (popups) et les ancres « #section » restent autorisés.
        /// Seule la navigation par défaut est annulée (preventDefault) : la propagation n'est jamais stoppée,
        /// donc tous les gestionnaires JavaScript du prototype s'exécutent normalement.
        /// </summary>
        private const string NavigationGuardScript = """
            <script id="ismoc-nav-guard">document.addEventListener('submit',function(e){if(!e.defaultPrevented){e.preventDefault();}});document.addEventListener('click',function(e){var a=e.target&&e.target.closest?e.target.closest('a[href]'):null;if(!a||e.defaultPrevented||a.target==='_blank')return;var h=(a.getAttribute('href')||'').trim();if(h==='#'||h===''||!h.startsWith('#')){e.preventDefault();}});</script>
            """;

        private const string DefaultFontsMarkup = """
            <link rel="preconnect" href="https://fonts.googleapis.com"><link rel="preconnect" href="https://fonts.gstatic.com" crossorigin><link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=Plus+Jakarta+Sans:wght@500;600;700;800&display=swap" rel="stylesheet"><style id="ismoc-fonts">body{font-family:'Inter',ui-sans-serif,system-ui,sans-serif}h1,h2,h3,h4{font-family:'Plus Jakarta Sans','Inter',ui-sans-serif,sans-serif}</style>
            """;

        private static readonly JsonSerializerOptions PromptSerializerOptions = new(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        /// <summary>Délai maximal d'une génération (Anthropic : Timeout du HttpClient dédié ; OpenAI : NetworkTimeout).</summary>
        public static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(4);

        private readonly IConfiguration _configuration;
        private readonly AnthropicMessagesClient _anthropicClient;
        private readonly ILogger<OpenAiMvpService> _logger;

        public OpenAiMvpService(IConfiguration configuration, AnthropicMessagesClient anthropicClient, ILogger<OpenAiMvpService> logger)
        {
            _configuration = configuration;
            _anthropicClient = anthropicClient;
            _logger = logger;
        }

        private string DesignModel => string.IsNullOrWhiteSpace(_configuration["OpenAI:DesignModel"]) ? DefaultDesignModel : _configuration["OpenAI:DesignModel"]!;
        private string UtilityModel => string.IsNullOrWhiteSpace(_configuration["OpenAI:UtilityModel"]) ? DefaultUtilityModel : _configuration["OpenAI:UtilityModel"]!;

        /// <summary>« OpenAI:MaxOutputTokens » (16 384 = maximum de sortie de gpt-4o).</summary>
        private int MaxOutputTokens =>
            int.TryParse(_configuration["OpenAI:MaxOutputTokens"], out var value) && value is > 0 and <= DefaultMaxOutputTokens ? value : DefaultMaxOutputTokens;

        /// <summary>« OpenAI:Temperature » (0.7 par défaut).</summary>
        private float Temperature =>
            float.TryParse(_configuration["OpenAI:Temperature"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && value is >= 0f and <= 2f
                ? value
                : DefaultTemperature;

        public Task<string> GenerateMvpHtmlAsync(string initialIdea, ReferenceImage? image = null)
        {
            var prompt = JsonSerializer.Serialize(new { initialIdea, referenceImageAttached = image is not null }, PromptSerializerOptions);
            return GenerateHtmlAsync(HtmlGenerationPrompt, prompt, image);
        }

        public Task<string> GenerateManualMvpHtmlAsync(GenerateBriefDto brief, ReferenceImage? image = null)
        {
            ArgumentNullException.ThrowIfNull(brief);
            var prompt = JsonSerializer.Serialize(new
            {
                format = brief.Format,
                sector = brief.Sector,
                primaryColor = brief.PrimaryColor,
                secondaryColor = brief.SecondaryColor,
                pagesAndModules = brief.Modules,
                description = string.IsNullOrWhiteSpace(brief.Description) ? null : brief.Description.Trim(),
                referenceImageAttached = image is not null
            }, PromptSerializerOptions);
            return GenerateHtmlAsync(ManualGenerationPrompt, prompt, image);
        }

        public async Task<string> RefineMvpAsync(ProjectMemoryDto memory, string instruction, ReferenceImage? image = null)
        {
            var prompt = JsonSerializer.Serialize(new
            {
                memory.InitialIdea,
                memory.UserPreferences,
                memory.CurrentHtml,
                ChatHistory = RecentHistory(memory),
                instruction,
                referenceImageAttached = image is not null
            }, PromptSerializerOptions);
            var result = await RequestRefineAsync(prompt, image);

            // Une retouche valide est acceptée ; un net appauvrissement est seulement signalé dans les logs.
            var refinedLength = RefinedHtmlLength(result);
            var currentLength = memory.CurrentHtml?.Length ?? 0;
            if (refinedLength > 0 && currentLength > 0 && refinedLength < currentLength * 0.6)
            {
                _logger.LogWarning("Retouche du projet {ProjectId} nettement plus courte que la maquette actuelle ({Current} → {Refined} caractères).", memory.ProjectId, currentLength, refinedLength);
            }

            return result;
        }

        private async Task<string> RequestRefineAsync(string prompt, ReferenceImage? image)
        {
            const string schema = "{ reply: string, designPlan: string[] | null, html: complete HTML document string | null, extractedPreference: string | null }";
            string response;
            try
            {
                response = await RequestJsonAsync(HtmlRefinePrompt, prompt, AiPurpose.Design, image, RefineSchema());
            }
            catch (AiTruncatedResponseException truncated)
            {
                // Coupure à max_tokens pendant une retouche : on récupère la maquette et la réponse déjà écrites.
                var recovered = TryRecoverTruncatedHtml(truncated.PartialText);
                if (recovered is null) throw; // contenu inexploitable : erreur 413 exacte renvoyée
                var reply = ExtractPartialJsonString(truncated.PartialText, "reply", requireClosed: true)
                    ?? "La maquette a été mise à jour.";
                _logger.LogWarning("Retouche tronquée par {Provider} : maquette récupérée automatiquement ({Length} caractères).", truncated.Provider, recovered.Length);
                return JsonSerializer.Serialize(new { reply, html = NormalizeHtml(recovered), extractedPreference = (string?)null }, PromptSerializerOptions);
            }

            var root = await ParseObjectWithRepairAsync(response, schema);

            try
            {
                return ValidateRefineResponse(root);
            }
            catch (JsonException exception)
            {
                var repaired = await RepairResponseAsync(response, schema, exception.Message);
                return ValidateRefineResponse(await ParseObjectWithRepairAsync(repaired, schema));
            }
        }

        private static int RefinedHtmlLength(string refineJson)
        {
            using var document = JsonDocument.Parse(refineJson);
            return document.RootElement.TryGetProperty("html", out var html) && html.ValueKind == JsonValueKind.String ? html.GetString()!.Length : 0;
        }

        public async Task<string> EstimateMvpAsync(ProjectMemoryDto memory)
        {
            var prompt = JsonSerializer.Serialize(new
            {
                memory.InitialIdea,
                memory.UserPreferences,
                memory.CurrentHtml,
                ChatHistory = RecentHistory(memory)
            }, PromptSerializerOptions);
            const string schema = "{ projectName: string, projectType: 'web'|'mobile', complexity: string, developmentPriceEur: int, designIdentityPriceEur: int, monthlyMaintenanceEur: int, estimatedDays: int, screens: string[], features: string[], deliveryTime: { minDays: int, maxDays: int, summary: string }, breakdown: [{ workstream: string, minDays: int, maxDays: int, minPriceEur: int, maxPriceEur: int }], totalBudgetEur: { min: int, max: int }, summary: string }";
            var response = await RequestJsonAsync(EstimatePrompt, prompt, AiPurpose.Utility);
            var estimate = await ParseObjectWithRepairAsync(response, schema);

            try
            {
                ValidateEstimate(estimate);
            }
            catch (JsonException exception)
            {
                estimate = await ParseObjectWithRepairAsync(await RepairResponseAsync(response, schema, exception.Message), schema);
                ValidateEstimate(estimate);
            }

            return estimate.GetRawText();
        }

        /// <summary>
        /// Décode une image envoyée en data URL ou en base64 brut. Retourne null si la valeur est vide ;
        /// lève ArgumentException si elle est invalide, trop lourde ou d'un format non pris en charge.
        /// </summary>
        public static ReferenceImage? DecodeReferenceImage(string? value, int maxBytes = 5 * 1024 * 1024)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var mediaType = "image/png";
            var payload = value.Trim();
            var dataUrl = Regex.Match(payload, @"^data:(?<type>image/[a-zA-Z0-9.+-]+);base64,(?<data>.+)$", RegexOptions.Singleline);
            if (dataUrl.Success)
            {
                mediaType = dataUrl.Groups["type"].Value.ToLowerInvariant();
                payload = dataUrl.Groups["data"].Value;
            }

            if (mediaType is not ("image/png" or "image/jpeg" or "image/jpg" or "image/webp" or "image/gif"))
            {
                throw new ArgumentException("Format d'image non pris en charge (PNG, JPEG, WebP ou GIF).");
            }

            if (payload.Length > maxBytes / 3 * 4 + 8) throw new ArgumentException($"L'image dépasse {maxBytes / (1024 * 1024)} Mo.");

            try
            {
                var bytes = Convert.FromBase64String(payload);
                return new ReferenceImage(bytes, mediaType == "image/jpg" ? "image/jpeg" : mediaType);
            }
            catch (FormatException)
            {
                throw new ArgumentException("L'image transmise n'est pas un base64 valide.");
            }
        }

        /// <summary>
        /// Génère la maquette. Toute maquette valide (balise &lt;body&gt; et plus de <see cref="MinimumHtmlLength"/> caractères)
        /// est acceptée telle quelle : les écarts au standard de design sont seulement journalisés (pas de 2e génération).
        /// </summary>
        private async Task<string> GenerateHtmlAsync(string systemPrompt, string userPrompt, ReferenceImage? image)
        {
            var html = await RequestHtmlAsync(systemPrompt, userPrompt, image);
            var issues = AssessQuality(html);
            if (issues.Count > 0)
            {
                _logger.LogInformation("Maquette acceptée avec {Count} écart(s) au standard de design : {Issues}", issues.Count, string.Join(" | ", issues));
            }

            return html;
        }

        /// <summary>Appel au modèle de design et extraction du HTML (le « designPlan » sert de raisonnement préalable).</summary>
        private async Task<string> RequestHtmlAsync(string systemPrompt, string userPrompt, ReferenceImage? image)
        {
            const string schema = "{ designPlan: string[] (6 à 8 entrées), html: string } document HTML5 complet avec Tailwind CDN";
            string response;
            try
            {
                response = await RequestJsonAsync(systemPrompt, userPrompt, AiPurpose.Design, image, GenerationSchema());
            }
            catch (AiTruncatedResponseException truncated)
            {
                // Coupure à max_tokens : maquette exploitable récupérée et refermée plutôt qu'une erreur 413.
                var recovered = TryRecoverTruncatedHtml(truncated.PartialText);
                if (recovered is null) throw; // contenu inexploitable : erreur 413 exacte renvoyée
                _logger.LogWarning("Maquette tronquée par {Provider} : récupérée automatiquement ({Length} caractères, balises refermées).", truncated.Provider, recovered.Length);
                return NormalizeHtml(recovered);
            }

            var root = await ParseObjectWithRepairAsync(response, schema);

            try
            {
                return NormalizeHtml(ReadRequiredString(root, "html"));
            }
            catch (JsonException exception)
            {
                var repaired = await RepairResponseAsync(response, schema, exception.Message);
                var repairedRoot = await ParseObjectWithRepairAsync(repaired, schema);
                return NormalizeHtml(ReadRequiredString(repairedRoot, "html"));
            }
        }

        /// <summary>
        /// Contrôle qualité automatique d'une maquette. Retourne la liste des défauts (vide si conforme).
        /// La longueur en caractères sert d'indicateur (≈ 250 lignes), le modèle pouvant compacter ses lignes.
        /// </summary>
        public static List<string> AssessQuality(string html)
        {
            var issues = new List<string>();
            var isMobile = html.Contains("w-[390px]", StringComparison.Ordinal) || html.Contains("rounded-[44px]", StringComparison.Ordinal);
            var lineCount = html.Count(character => character == '\n') + 1;

            if (html.Length < (isMobile ? 9_000 : 15_000))
            {
                issues.Add($"Document trop court ({lineCount} lignes, {html.Length} caractères) : vise 350 à 700 lignes indentées couvrant toutes les sections obligatoires.");
            }

            if (!html.Contains("addEventListener", StringComparison.Ordinal) && !Regex.IsMatch(html, @"\bonclick\s*=", RegexOptions.IgnoreCase))
            {
                issues.Add("Aucune interactivité JavaScript : ajoute un script complet avant </body> (vues, filtres, modales, parcours de paiement, toasts).");
            }

            if (!isMobile)
            {
                if (!html.Contains("sticky", StringComparison.Ordinal)) issues.Add("Navbar sticky (backdrop-blur-md bg-white/90) absente.");
                if (!html.Contains("<footer", StringComparison.OrdinalIgnoreCase)) issues.Add("Footer multi-colonnes (bg-slate-950 text-slate-300) absent.");
                if (Regex.Matches(html, @"<section\b", RegexOptions.IgnoreCase).Count < 5)
                {
                    issues.Add("Moins de 5 sections : il faut Hero 2 colonnes, grille d'offres, parcours secondaire avec paiement, « Comment ça marche », confiance & avis.");
                }
            }
            else if (Regex.Matches(html, @"data-(?:tab|screen|view)\b", RegexOptions.IgnoreCase).Count < 3 && Regex.Matches(html, @"id=""(?:screen|view|tab)-", RegexOptions.IgnoreCase).Count < 3)
            {
                issues.Add("La barre d'onglets doit naviguer entre 3 à 4 écrans complets (ex. id=\"screen-accueil\" ou data-screen).");
            }

            if (html.Contains("#65a30d", StringComparison.OrdinalIgnoreCase)) issues.Add("Couleur criarde interdite (#65a30d) : utilise une palette harmonieuse.");

            if (!isMobile && Regex.Matches(html, @"data-view\s*=", RegexOptions.IgnoreCase).Count < 3)
            {
                issues.Add("Architecture SPA absente : il faut plusieurs vues commutables <section data-view=\"home|explore|create|checkout|account\"> reliées par data-nav.");
            }

            // Les href="#" ne sont plus un défaut : NormalizeHtml les remplace par href="javascript:void(0)".
            return issues;
        }

        private static IEnumerable<ChatMessageDto> RecentHistory(ProjectMemoryDto memory) =>
            memory.ChatHistory.Count <= MaxHistoryMessages
                ? memory.ChatHistory
                : memory.ChatHistory.Skip(memory.ChatHistory.Count - MaxHistoryMessages);

        /// <summary>Usage d'un appel IA : conception d'interface (gros document HTML) ou tâche utilitaire (devis, réparation JSON).</summary>
        private enum AiPurpose
        {
            /// <summary>Génération et retouches de maquettes.</summary>
            Design,

            /// <summary>Réparation d'un JSON mal formé : même fournisseur que la conception (Claude si configuré).</summary>
            Repair,

            /// <summary>Devis : OpenAI (gpt-4o-mini) s'il est configuré.</summary>
            Utility
        }

        public bool IsConfigured => AnthropicApiKey is not null || OpenAiApiKey is not null;

        private string? AnthropicApiKey => string.IsNullOrWhiteSpace(_configuration["Anthropic:ApiKey"]) ? null : _configuration["Anthropic:ApiKey"];
        private string? OpenAiApiKey => string.IsNullOrWhiteSpace(_configuration["OpenAI:ApiKey"]) ? null : _configuration["OpenAI:ApiKey"];

        private string AnthropicDesignModel => string.IsNullOrWhiteSpace(_configuration["Anthropic:DesignModel"]) ? DefaultAnthropicDesignModel : _configuration["Anthropic:DesignModel"]!;
        private string AnthropicUtilityModel => string.IsNullOrWhiteSpace(_configuration["Anthropic:UtilityModel"]) ? DefaultAnthropicUtilityModel : _configuration["Anthropic:UtilityModel"]!;

        /// <summary>« Anthropic:MaxTokens » (32 768 par défaut ; possible grâce au streaming).</summary>
        private int AnthropicMaxOutputTokens =>
            int.TryParse(_configuration["Anthropic:MaxTokens"], out var value) && value > 0 ? value : DefaultAnthropicMaxTokens;

        /// <summary>
        /// Routage des fournisseurs :
        /// - génération, retouches et réparation du JSON des maquettes : EXCLUSIVEMENT Claude dès que « Anthropic:ApiKey »
        ///   est renseignée — aucune bascule sur OpenAI ; en cas d'échec, l'erreur exacte d'Anthropic (journalisée par
        ///   AnthropicMessagesClient) remonte telle quelle jusqu'au client. Sans clé Anthropic : OpenAI (gpt-4o) ;
        /// - devis (utilitaire) : OpenAI (gpt-4o-mini) s'il est configuré, sinon Claude.
        /// </summary>
        private async Task<string> RequestJsonAsync(string systemPrompt, string userPrompt, AiPurpose purpose, ReferenceImage? image = null, JsonObject? jsonSchema = null)
        {
            var anthropicKey = AnthropicApiKey;
            var openAiKey = OpenAiApiKey;
            if (anthropicKey is null && openAiKey is null)
            {
                throw new InvalidOperationException("Aucune clé IA n'est configurée. Définissez Anthropic:ApiKey (prioritaire) ou OpenAI:ApiKey avec User Secrets ou les variables Anthropic__ApiKey / OpenAI__ApiKey.");
            }

            var useAnthropic = purpose == AiPurpose.Utility ? openAiKey is null : anthropicKey is not null;
            if (!useAnthropic)
            {
                return await RequestOpenAiAsync(systemPrompt, userPrompt, purpose == AiPurpose.Utility ? UtilityModel : DesignModel, openAiKey!, image);
            }

            var model = purpose == AiPurpose.Utility ? AnthropicUtilityModel : AnthropicDesignModel;

            // Aucun catch : une AiProviderException (code HTTP, type et message exacts d'Anthropic) est renvoyée au contrôleur.
            var text = await _anthropicClient.CreateMessageAsync(anthropicKey!, model, AnthropicMaxOutputTokens, systemPrompt, userPrompt, image, jsonSchema);
            return StripMarkdown(text);
        }

        private async Task<string> RequestOpenAiAsync(string systemPrompt, string userPrompt, string model, string apiKey, ReferenceImage? image)
        {
            // Délai réseau aligné sur les générations riches (le délai par défaut du SDK coupe à 100 s).
            var client = new ChatClient(model, new ApiKeyCredential(apiKey), new OpenAIClientOptions { NetworkTimeout = GenerationTimeout });
            UserChatMessage userMessage = image is null
                ? new UserChatMessage(userPrompt)
                : new UserChatMessage(
                    ChatMessageContentPart.CreateTextPart(userPrompt),
                    ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(image.Data), image.MediaType, ChatImageDetailLevel.High));

            var messages = new ChatMessage[] { new SystemChatMessage(systemPrompt), userMessage };
            var options = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
                MaxOutputTokenCount = MaxOutputTokens,
                Temperature = Temperature
            };

            var completion = await client.CompleteChatAsync(messages, options);
            if (completion.Value.FinishReason == ChatFinishReason.Length)
            {
                // Même traitement que Claude : l'appelant tente de récupérer la maquette partielle.
                throw new AiTruncatedResponseException("OpenAI", completion.Value.Content.FirstOrDefault()?.Text ?? string.Empty);
            }

            var response = completion.Value.Content.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(response)) throw new InvalidOperationException("OpenAI a retourné une réponse vide.");
            return StripMarkdown(response);
        }

        /// <summary>
        /// Retire les balises markdown (```json … ```) et tout texte parasite autour de l'objet JSON.
        /// </summary>
        public static string StripMarkdown(string response)
        {
            var text = response.Trim().TrimStart('﻿');
            if (text.StartsWith('{')) return text;

            var fence = Regex.Match(text, @"```(?:json|JSON)?\s*(\{.*\})\s*```", RegexOptions.Singleline);
            if (fence.Success) return fence.Groups[1].Value.Trim();

            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            return start > 0 && end > start ? text[start..(end + 1)] : text;
        }

        private async Task<JsonElement> ParseObjectWithRepairAsync(string response, string expectedSchema)
        {
            response = StripMarkdown(response);
            try
            {
                using var document = JsonDocument.Parse(response);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("La réponse doit être un objet JSON.");
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                var repaired = await RepairResponseAsync(response, expectedSchema, "La réponse n'était pas un objet JSON syntaxiquement valide.");
                using var repairedDocument = JsonDocument.Parse(repaired);
                if (repairedDocument.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("La réparation n'a pas produit un objet JSON.");
                return repairedDocument.RootElement.Clone();
            }
        }

        private Task<string> RepairResponseAsync(string response, string expectedSchema, string validationError)
        {
            var repairRequest = JsonSerializer.Serialize(new { expectedSchema, validationError, invalidResponse = response }, PromptSerializerOptions);
            return RequestJsonAsync(JsonRepairPrompt, repairRequest, AiPurpose.Repair);
        }

        private static string ValidateRefineResponse(JsonElement root)
        {
            var reply = ReadRequiredString(root, "reply");

            string? html = null;
            if (root.TryGetProperty("html", out var htmlElement))
            {
                html = htmlElement.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.String when string.IsNullOrWhiteSpace(htmlElement.GetString())
                        || string.Equals(htmlElement.GetString(), "null", StringComparison.OrdinalIgnoreCase) => null,
                    JsonValueKind.String => NormalizeHtml(htmlElement.GetString()!),
                    _ => throw new JsonException("Le champ html doit être du HTML ou null.")
                };
            }

            string? extractedPreference = null;
            if (root.TryGetProperty("extractedPreference", out var preferenceElement) && preferenceElement.ValueKind == JsonValueKind.String)
            {
                var value = preferenceElement.GetString()?.Trim();
                extractedPreference = string.IsNullOrWhiteSpace(value) || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) ? null : value;
            }

            return JsonSerializer.Serialize(new { reply, html, extractedPreference }, PromptSerializerOptions);
        }

        private static string ReadRequiredString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(property.GetString()))
            {
                throw new JsonException($"Le champ {propertyName} est obligatoire.");
            }

            return property.GetString()!;
        }

        /// <summary>
        /// Valide le document puis garantit de façon déterministe le charset UTF-8 et le fallback d'images.
        /// </summary>
        private static string NormalizeHtml(string html)
        {
            html = html.Trim();
            if (html.StartsWith("```", StringComparison.Ordinal))
            {
                html = Regex.Replace(html, @"^```[a-zA-Z]*\s*|\s*```$", string.Empty).Trim();
            }

            if (html.Length > 1_500_000) throw new JsonException("La maquette dépasse la taille maximale autorisée.");

            // Seule règle bloquante (protection contre l'écran blanc) : une balise <body> et plus de MinimumHtmlLength
            // caractères. Sinon la maquette précédente est conservée par l'appelant.
            if (!html.Contains("<body", StringComparison.OrdinalIgnoreCase) || html.Length < MinimumHtmlLength)
            {
                throw new JsonException($"La maquette générée est vide ou incomplète (balise <body> absente ou moins de {MinimumHtmlLength} caractères).");
            }

            // Liens morts corrigés automatiquement au lieu de rejeter la génération.
            html = DeadLink.Replace(html, "href=\"javascript:void(0)\"");

            // Document toujours autonome et en mode standard.
            if (!html.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)) html = "<!DOCTYPE html>\n" + html;

            // <head> absent : on en crée un juste avant <body> pour y injecter les ressources nécessaires.
            var headMatch = Regex.Match(html, @"<head[^>]*>", RegexOptions.IgnoreCase);
            if (!headMatch.Success)
            {
                var bodyIndex = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
                html = html.Insert(bodyIndex, "<head></head>\n");
                headMatch = Regex.Match(html, @"<head[^>]*>", RegexOptions.IgnoreCase);
            }

            var injection = string.Empty;
            // Tailwind CDN ajouté s'il manque (au lieu de rejeter la maquette).
            if (!html.Contains("cdn.tailwindcss.com", StringComparison.OrdinalIgnoreCase)) injection += "\n<script src=\"https://cdn.tailwindcss.com\"></script>";
            if (!Regex.IsMatch(html, @"<meta[^>]+charset", RegexOptions.IgnoreCase)) injection += "\n<meta charset=\"UTF-8\">";
            if (!html.Contains("ismoc-img-fallback", StringComparison.Ordinal)) injection += "\n" + ImageFallbackScript.Trim();

            // Polices par défaut (Plus Jakarta Sans + Inter) si le modèle ne les a pas chargées.
            if (!html.Contains("fonts.googleapis.com", StringComparison.OrdinalIgnoreCase)) injection += "\n" + DefaultFontsMarkup.Trim();

            // Icônes Lucide : bibliothèque ajoutée si absente, et rendu automatique des <i data-lucide> (y compris ajoutés
            // dynamiquement) même si le prototype oublie d'appeler lucide.createIcons().
            if (html.Contains("data-lucide", StringComparison.Ordinal))
            {
                if (!html.Contains("unpkg.com/lucide", StringComparison.OrdinalIgnoreCase)) injection += "\n<script src=\"https://unpkg.com/lucide@latest\"></script>";
                if (!html.Contains("ismoc-lucide", StringComparison.Ordinal)) injection += "\n" + LucideAutoRenderScript.Trim();
            }

            // Garde de navigation : un formulaire soumis ou un lien vers une autre page ferait naviguer l'iframe
            // d'aperçu hors du document (écran blanc). Ce script garde le prototype affiché.
            if (!html.Contains("ismoc-nav-guard", StringComparison.Ordinal)) injection += "\n" + NavigationGuardScript.Trim();

            return injection.Length == 0 ? html : html.Insert(headMatch.Index + headMatch.Length, injection);
        }

        private static void ValidateEstimate(JsonElement estimate)
        {
            if (!estimate.TryGetProperty("complexity", out var complexity) || complexity.ValueKind != JsonValueKind.String
                || !estimate.TryGetProperty("deliveryTime", out var deliveryTime) || deliveryTime.ValueKind != JsonValueKind.Object
                || !deliveryTime.TryGetProperty("minDays", out var minDays) || !minDays.TryGetInt32(out _)
                || !deliveryTime.TryGetProperty("maxDays", out var maxDays) || !maxDays.TryGetInt32(out _)
                || !estimate.TryGetProperty("breakdown", out var breakdown) || breakdown.ValueKind != JsonValueKind.Array
                || !estimate.TryGetProperty("totalBudgetEur", out var totalBudget) || totalBudget.ValueKind != JsonValueKind.Object
                || !totalBudget.TryGetProperty("min", out var minimum) || !minimum.TryGetInt32(out _)
                || !totalBudget.TryGetProperty("max", out var maximum) || !maximum.TryGetInt32(out _)
                || !estimate.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("Le devis retourné ne respecte pas le schéma attendu.");
            }

            foreach (var priceField in new[] { "developmentPriceEur", "designIdentityPriceEur", "monthlyMaintenanceEur", "estimatedDays" })
            {
                if (!estimate.TryGetProperty(priceField, out var value) || !value.TryGetInt32(out var number) || number <= 0)
                {
                    throw new JsonException($"Le champ {priceField} doit être un entier strictement positif (montants en euros).");
                }
            }

            foreach (var row in breakdown.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object
                    || !row.TryGetProperty("workstream", out var workstream) || workstream.ValueKind != JsonValueKind.String
                    || !row.TryGetProperty("minDays", out var rowMinDays) || !rowMinDays.TryGetInt32(out _)
                    || !row.TryGetProperty("maxDays", out var rowMaxDays) || !rowMaxDays.TryGetInt32(out _)
                    || !row.TryGetProperty("minPriceEur", out var rowMin) || !rowMin.TryGetInt32(out _)
                    || !row.TryGetProperty("maxPriceEur", out var rowMax) || !rowMax.TryGetInt32(out _))
                {
                    throw new JsonException("Un poste du devis ne respecte pas le schéma attendu.");
                }
            }
        }
    }
}
