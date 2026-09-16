using NAudio.Wave;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// Automatically generates a baseline voice fingerprint for the currently loaded Piper TTS model.
/// OpenVoice uses this source-speaker tone embedding as the neutral point from which it shifts
/// the Piper voice toward a target cloned voice.
/// </summary>
public class BaseVoiceGenerator(
    UnifiedPhonemizer phonemizer,
    PiperRunner piperRunner,
    AudioProcessor audioProcessor,
    OpenVoiceRunner openVoice,
    PiperConfig piperConfig,
    ClonerSettings clonerConfig)
{
    private readonly UnifiedPhonemizer _phonemizer = phonemizer;
    private readonly PiperRunner _piperRunner = piperRunner;
    private readonly AudioProcessor _audioProcessor = audioProcessor;
    private readonly OpenVoiceRunner _openVoice = openVoice;
    private readonly PiperConfig _piperConfig = piperConfig;
    private readonly ClonerSettings _clonerConfig = clonerConfig;

    // OpenVoice does not publish a hard minimum duration; it only warns against references
    // that are too short. This is a Tsubaki diagnostic threshold, not an OpenVoice requirement.
    private const double RecommendedMinimumReferenceSeconds = 8.0;

    // Short articulatory probes are appended to many natural passages. Their purpose is not
    // linguistic meaning; they expose the base Piper voice to varied vowel/consonant transitions
    // and give OpenVoice a more representative tone-color sample.
    private const string LatinProbe = "Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, va, sa, za; ra, la, ya, wa.";
    private const string CyrillicProbe = "Ма, ме, ми, мо, му; на, не, ни, но, ну; па, ба, та, да, ка, га; фа, ва, са, за; ша, жа, ча, ра, ла, я, ва.";
    private const string ArmenianProbe = "Մա, մե, մի, մո, մու․ նա, նե, նի, նո, նու․ պա, բա, տա, դա, կա, գա․ ֆա, վա, սա, զա, շա, ժա, րա, լա, յա։";
    private const string GeorgianProbe = "მა, მე, მი, მო, მუ; ნა, ნე, ნი, ნო, ნუ; პა, ბა, ტა, და, კა, გა; ფა, ვა, სა, ზა, შა, ჟა, რა, ლა, ია.";
    private const string GreekProbe = "Μα, με, μι, μο, μου· να, νε, νι, νο, νου· πα, βα, τα, δα, κα, γα· φα, θα, σα, ζα, χα, ρα, λα, για.";
    private const string ArabicScriptProbe = "ما، مي، مو؛ نا، ني، نو؛ پا، با، تا، دا، كا، گا؛ فا، وا، سا، زا، شا، جا، را، لا، يا.";
    private const string HebrewProbe = "מָה, מִי, מוּ; נָה, נִי, נוּ; פָּה, בָּה, תָּה, דָּה, קָה, גָּה; פָה, סָה, זָה, שָׁה, רָה, לָה, יָה, וָה.";
    private const string DevanagariProbe = "मा, मे, मि, मो, मु; ना, ने, नि, नो, नु; पा, बा, ता, दा, टा, डा, का, गा; फा, सा, शा, जा, रा, ला, या, वा.";
    private const string BengaliProbe = "মা, মে, মি, মো, মু; না, নে, নি, নো, নু; পা, বা, তা, দা, কা, গা; ফা, সা, শা, জা, রা, লা, যা, ওয়া.";
    private const string GujaratiProbe = "મા, મે, મિ, મો, મુ; ના, ને, નિ, નો, નુ; પા, બા, તા, દા, ટા, ડા, કા, ગા; ફા, સા, ઝા, શા, રા, લા, યા, વા.";
    private const string GurmukhiProbe = "ਮਾ, ਮੇ, ਮਿ, ਮੋ, ਮੁ; ਨਾ, ਨੇ, ਨਿ, ਨੋ, ਨੁ; ਪਾ, ਬਾ, ਤਾ, ਦਾ, ਟਾ, ਡਾ, ਕਾ, ਗਾ; ਫਾ, ਸਾ, ਸ਼ਾ, ਜਾ, ਰਾ, ਲਾ, ਯਾ, ਵਾ.";
    private const string HangulProbe = "마, 메, 미, 모, 무; 나, 네, 니, 노, 누; 파, 바, 타, 다, 카, 가; 사, 자, 차, 하, 라, 야, 와.";
    private const string TamilProbe = "மா, மே, மி, மோ, மு; நா, நே, நி, நோ, நு; பா, பி, தா, டா, கா, கி; சா, ஜா, ரா, லா, யா, வா.";
    private const string TeluguProbe = "మా, మే, మి, మో, ము; నా, నే, ని, నో, ను; పా, బా, తా, దా, టా, డా, కా, గా; ఫా, సా, శా, జా, రా, లా, యా, వా.";
    private const string ThaiProbe = "มา มี มู เม โม; นา นี นู เน โน; ปา บา ตา ดา กา คา; ฟา วา ซา จา ชา รา ลา ยา วา.";

    // This table targets the current eSpeak-ng language frontends, including development/WIP
    // identifiers and constructed languages that Piper may never ship as trained voice models.
    // Accent-only variants are added below as aliases to the same text: the active eSpeak voice
    // still supplies the accent when UnifiedPhonemizer turns the reference into phonemes.
    //
    // Special frontends deliberately keep their required writing systems:
    // - ar: fully diacritized Arabic
    // - ja: Hiragana/Katakana only (no Kanji)
    // - cmn-latn-pinyin: Pinyin
    // - yue-latn-jyutping: Jyutping
    // - en-shaw: Shavian
    // - chr: best-effort probe for eSpeak-ng's experimental Cherokee DF frontend. Ordinary
    //        Cherokee syllabary / plain romanized text is not a fully supported input path.
    private static readonly Dictionary<string, string> _referenceTexts = CreateReferenceTexts();

    private static Dictionary<string, string> CreateReferenceTexts()
    {
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ab"] = "Ма, ми, му; на, ни, ну; па, ба, та, да, ка, га; фа, ва, са, за; ша, жа, ча, ра, ла, йа, уа. Ауа, уиа, аиа; ам, ан, ар, ал.",
            ["af"] = "Piet smyt vyf skuins bokse vir die kwikstertjie. Jakkals sjampanje wals zyl. " + LatinProbe,
            ["am"] = "ማ ሚ ሙ ሜ ሞ፣ ና ኒ ኑ ኔ ኖ፣ ፓ ባ ታ ዳ ካ ጋ፣ ፋ ቫ ሳ ዛ ሻ ጃ፣ ራ ላ ያ ዋ። ብሩህ ድምፅ በቀስታ እና በግልጽ ሁኔታ ይናገራል።",
            ["an"] = "Un raboso rapido cruza o campo mientras o viento mueve as fuellas. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ca, ga; fa, va, sa, za, cha, ra, la.",
            ["ar"] = "هٰذَا صَوْتٌ وَاضِحٌ وَهَادِئٌ، يَقْرَأُ جُمَلًا قَصِيرَةً وَطَوِيلَةً بِإِيقَاعٍ طَبِيعِيٍّ. مَا، مِي، مُو؛ نَا، نِي، نُو؛ بَا، تَا، دَا، كَا، جَا؛ فَا، سَا، زَا، شَا، رَا، لَا، يَا، وَوَا.",
            ["as"] = "মা, মি, মু, মে, মো; না, নি, নু, নে, নো; পা, বা, তা, দা, কা, গা; ফা, সা, শা, জা, ৰা, লা, ৱা। এটা স্পষ্ট কণ্ঠই ধীৰে আৰু স্বাভাৱিক ছন্দত বহু ধৰণৰ শব্দ কয়।",
            ["az"] = "Zəfər, cəsur qəhrəman, şadlıqla və xüsusi coşqu ilə fəxr etdi. Böyük jürinin rəyi qətiyyətlidir. " + LatinProbe,
            ["ba"] = "Ма, ми, му; на, ни, ну; па, ба, та, да, ка, га; фа, ва, са, за; ша, жа, ча, ра, ла, йа, уа. Йылы ел тауҙар аша иҫә, ә асыҡ тауыш төрлө өндәрҙе тыныс әйтә.",
            ["be"] = "У Іўі худы жвавы чорт у зялёнай камізэльцы пабег пад елку. Шчупак дзяўбнуў кручок. " + CyrillicProbe,
            ["bg"] = "Жълтата дюля беше щастлива, че пухкавият зайчо ѝ подари цвете. Шофьорът търси път. " + CyrillicProbe,
            ["bn"] = "বিড়ালটা হঠাৎ লাফিয়ে উঠে সাদা ইঁদুরটিকে ধরে ফেলল। মেঘলা দিনে বৃষ্টি শুরু হলো। " + BengaliProbe,
            ["bpy"] = "মা, মি, মু, মে, মো; না, নি, নু, নে, নো; পা, বা, তা, দা, কা, গা; ফা, সা, শা, জা, রা, লা, বা। ধীরে বলা পরিষ্কার বাক্যে নরম ও শক্ত ধ্বনি একসঙ্গে শোনা যায়।",
            ["bs"] = "Fin džip, gluh jež i žut crv skučiše se na dnu. Ljubičasti slon brzo trči. " + LatinProbe,
            ["ca"] = "Jove xef, porti whisky amb quinze glaçons d'hidrogen, coi! La cigonya viatja al sud. " + LatinProbe,
            ["chr"] = "a, e, i, o, u, v; ga, ka, ge, gi, go, gu; da, ta, de, di, do, du; tsa, tla, hna, hla; ma, na, wa, ya, ra, la.",
            ["cmn"] = "今天天气晴朗，清晨的风轻轻吹过树林。小明骑着自行车经过桥边，听见鸟叫、流水和孩子们快乐的笑声。妈妈买了苹果、葡萄、青菜和热茶，大家慢慢说话。",
            ["cmn-latn-pinyin"] = "mā má mǎ mà, bā bá bǎ bà, dā dá dǎ dà, gā gá gǎ gà. jīntiān tiānqì hěn hǎo, qīngfēng chuīguò shùlín, háizi qīngqīng shuōhuà, péngyou kuàilè de xiàozhe.",
            ["crh"] = "Tez qoñur tilki yeşil bağçadan keçip, yel esken qıyıda yavaşça toqtadı. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga, qa; fa, va, sa, za, şa, ca, ra, la.",
            ["cs"] = "Příliš žluťoučký kůň úpěl ďábelské ódy. Hleď, toť stín, jak šíp běží podél stěny! " + LatinProbe,
            ["cv"] = "Ма, ми, му; на, ни, ну; па, ба, та, да, ка, га; фа, ва, са, за; ша, жа, ча, ра, ла, йа, уа. Таса сасӑ лӑпкӑн та уҫҫӑн калаҫать, вӑл тӗрлӗ сасӑсене пӗрлештерет.",
            ["cy"] = "Parciai ffenics blin gymaint â charlwm dew, dylluan fud, a rhinoseros gwyllt. " + LatinProbe,
            ["da"] = "Quizdeltagerne spiste jordbær med fløde, mens cirkusklovnen Walther spillede på xylofon. " + LatinProbe,
            ["de"] = "Falsches Üben von Xylophonmusik quält jeden größeren Zwerg. Victor jagt zwölf Boxkämpfer. " + LatinProbe,
            ["el"] = "Ταχίστη αλώπηξ βαφής ψημένη γη, δρασκελίζει υπέρ νωθρού κυνός. Το παιδί παίζει. " + GreekProbe,
            ["en"] = "The quick brown fox jumps over the lazy dog while a curious wizard quietly packs five bright boxes. Thin threads, rough gravel, buzzing bees, fresh cherries, warm rain, and cold wind move the voice through soft and sharp sounds. " + LatinProbe,
            ["en-shaw"] = "𐑞 𐑚𐑱𐑠 𐑣𐑿 𐑪𐑯 𐑞 𐑢𐑷𐑑𐑼𐑟 𐑝 𐑞 𐑤𐑪𐑣𐑒 𐑦𐑥𐑐𐑮𐑧𐑕𐑑 𐑷𐑤, 𐑦𐑯𐑒𐑤𐑵𐑛𐑦𐑙 𐑞 ·𐑓𐑮𐑧𐑯𐑗 𐑒𐑢𐑰𐑯, 𐑚𐑦𐑓𐑹 𐑖𐑰 𐑣𐑻𐑛 𐑞𐑨𐑑 𐑕𐑦𐑥𐑓𐑩𐑯𐑦 𐑩𐑜𐑧𐑯.",
            ["eo"] = "Eĥoŝanĝo ĉiuĵaŭde. Laŭ ebleco, ni provu uzi ĉiujn literojn de la alfabeto. " + LatinProbe,
            ["es"] = "El pingüino Wenceslao hizo kilómetros bajo exhaustiva lluvia y frío, añoraba a su querido cachorro. " + LatinProbe,
            ["et"] = "Põdur Zagrebi tšellomängija-följetonist Ciqku jääb haigeks, väljub žüriist. Jõgi voolab. " + LatinProbe,
            ["eu"] = "Zebra, azeri, txakur eta katu bat elkarrekin joan ziren mendira, poz-pozik abesten. " + LatinProbe,
            ["fa"] = "پیمان با یک گنجشک کوچک در خیابان ظفر قدم می‌زد و آواز می‌خواند. روباه قهوه‌ای سریع می‌پرد. " + ArabicScriptProbe,
            ["fa-latn"] = "ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga, qa; fa, va, sa, za, sha, zha, cha, ja; ra, la, ya. sedā-ye rowshan bā ārāmi jumle-hā-ye gunāgun rā mixānad.",
            ["fi"] = "Törkylempijävongahdus. Charles Darwin joi viskiä ja konjakkia fagottia soittaessaan. " + LatinProbe,
            ["fo"] = "Ein kvikur fuglur flýgur yvir grøna dalin, meðan kaldur vindur rørir sjógvin. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, va, sa, ra, la.",
            ["fr"] = "Portez ce vieux whisky au juge blond qui fume. Voix ambiguë d'un cœur qui au zéphyr préfère les jattes de kiwis. " + LatinProbe,
            ["ga"] = "D'ith an frog beag buí an chuileog mhór dhubh go tapa. D'fhéach an cat go géar air. " + LatinProbe,
            ["gd"] = "Tha guth soilleir a’ bruidhinn gu socair fhad ’s a tha gaoth fhuar a’ gluasad thar a’ ghleann. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ca, ga; fa, sa, sha, ra, la.",
            ["gn"] = "Peteĩ ñe’ẽ hesakãva oñe’ẽ mbeguekatu ha hekopete, yvytu oguata aja ka’aguy rupi. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; sa, ha, ra, la.",
            ["grc"] = "Ἄνδρα μοι ἔννεπε, Μοῦσα, πολύτροπον· φῶς δὲ λαμπρὸν ἐπὶ γαῖαν πίπτει. Μα, με, μι, μο, μυ· να, νε, νι, νο, νυ· πα, τα, κα, γα, φα, θα, σα, ρα, λα.",
            ["gu"] = "ઝડપી ભૂરો શિયાળ આળસુ કૂતરા પર કૂદે છે. પક્ષીઓ આકાશમાં સુંદર ગીતો ગાય છે. " + GujaratiProbe,
            ["hak"] = "今晡日天時當好，細風吹過樹林，細人仔在橋脣講話唱歌。山、水、風、花、鳥、月，聲音高低長短慢慢變化。",
            ["haw"] = "ʻO ka leo mālie e ʻōlelo ana i nā huaʻōlelo like ʻole i ke kakahiaka mālamalama. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ka, la, ha, wa, ʻa; ā, ē, ī, ō, ū.",
            ["he"] = "דג סקרן שט בים זך אך לפתע פגש חבורה נחמדה שצצה כך. איזה יום יפה היום. " + HebrewProbe,
            ["hi"] = "ऋषियों को सताने वाले दुष्ट राक्षसों के मरण पर देवता भी खुश थे। तेज़ भूरी लोमड़ी आलसी कुत्ते पर कूदती है। " + DevanagariProbe,
            ["hr"] = "Gojazni đačić s leptirićem demonstrirao je pejsažni crtež. Ljubičasta žaba leti. " + LatinProbe,
            ["ht"] = "Yon vwa klè ap pale dousman pandan van fre pase nan pyebwa yo. Ma, me, mi, mo, mou; na, ne, ni, no, nou; pa, ba, ta, da, ka, ga; fa, va, sa, za, cha, ja, ra, la.",
            ["hu"] = "Árvíztűrő tükörfúrógép. Egy hűtlen vejét fülöncsípő, dühös mexikói spanyol viador. " + LatinProbe,
            ["hy"] = "Ֆիզիկոս Գևորգը շուտով կգտնի ճշգրիտ ելքը, որը կօգնի լուծել այս բարդ խնդիրը։ " + ArmenianProbe,
            ["hyw"] = "Արեւմտահայ պարզ ձայնը հանդարտ կը խօսի, երբ մեղմ հովը ծառերուն մէջէն կ՚անցնի։ Մա, մի, մու, մե, մո․ նա, նի, նու, նե, նո․ պա, բա, տա, դա, կա, գա, ֆա, վա, սա, զա, շա, ժա, րա, լա։",
            ["ia"] = "Un voce clar parla calmemente durante que le vento move le folios del arbore. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ca, ga; fa, va, sa, za, cha, ja, ra, la.",
            ["id"] = "Jerapah belang yang lucu berjalan zigzag di taman safari xanadu bersama kawanannya. " + LatinProbe,
            ["io"] = "La klara voco parolas lente dum la vento movas la folii e la birdoj kantas. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, va, sa, za, cha, ja, ra, la.",
            ["is"] = "Kæmi ný öxi hér ykist þjófum nú bæði víl og ádrepa. Vaðlaheiðarvegavinnuverkfærageymsluskúraútidyralyklakippuhringur. " + LatinProbe,
            ["it"] = "Pranzo d'acqua fa volti sghembi. Ma la volpe col suo balzo ha raggiunto il quieto Fido. " + LatinProbe,
            ["ja"] = "まみむめも、なにぬねの、ぱぴぷぺぽ、ばびぶべぼ、がぎぐげご。きゃきゅきょ、しゃしゅしょ、ちゃちゅちょ。ざじずぜぞ、だぢづでど。きって、がっこう、しんぶん、スーパー、コーヒー、りょこう、びょういん。",
            ["jbo"] = "mi tavla do le nu le cladu voksa cu klama le traji valsi kei. mi cusku lo valsi pe la ma me mi mo mu, .e la pa ba ta da ka ga, .e la fa va sa za ra la.",
            ["ka"] = "ჩვეულებრივი, სწრაფი მელა გადახტა ზარმაცი ძაღლის თავზე. მზის სხივები ანათებს მთებს. " + GeorgianProbe,
            ["kaa"] = "Aşıq dawıs asıq hám tınıq aytıladı, samal dala arqalı jay esedi. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga, qa; fa, va, sa, za, sha, ja, ra, la.",
            ["kk"] = "Барлық адамдар тумысынан азат және қадір-қасиеті мен құқықтары тең болып дүниеге келеді. " + CyrillicProbe,
            ["kl"] = "Nipip ersarippoq, oqaatsillu assigiinngitsut eqqissillutik oqaatigineqarput. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ta, ka, qa; va, sa, ra, la, ja.",
            ["kn"] = "ಮಾ, ಮಿ, ಮು, ಮೇ, ಮೋ; ನಾ, ನಿ, ನು, ನೇ, ನೋ; ಪಾ, ಬಾ, ತಾ, ದಾ, ಟಾ, ಡಾ, ಕಾ, ಗಾ; ಫಾ, ಸಾ, ಶಾ, ಜಾ, ರಾ, ಲಾ, ಯಾ, ವಾ. ಸ್ಪಷ್ಟ ಧ್ವನಿ ನಿಧಾನವಾಗಿ ವಿವಿಧ ಪದಗಳನ್ನು ಹೇಳುತ್ತದೆ.",
            ["ko"] = "다람쥐 헌 쳇바퀴에 타고파. 키스의 고유조건은 입술끼리 만나야 하고 특별한 기술은 필요치 않다. " + HangulProbe,
            ["kok"] = "मा, मि, मु, मे, मो; ना, नि, नु, ने, नो; पा, बा, ता, दा, टा, डा, का, गा; फा, सा, शा, जा, रा, ला, या, वा. स्वच्छ आवाज हळुवारपणान वेगवेगळे शब्द उलयता.",
            ["ku"] = "Dengê zelal bi hêdî û bi awayekî xwezayî peyvên cuda dibêje, dema ba di nav daran de derbas dibe. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, va, sa, za, şa, ja, ra, la.",
            ["ky"] = "Ма, ми, му; на, ни, ну; па, ба, та, да, ка, га; фа, ва, са, за; ша, жа, ча, ра, ла, йа, уа. Тунук үн жай сүйлөп, ар түрдүү үндөрдү табигый ыргакта бириктирет.",
            ["la"] = "Sic fui, non sum; non fui, sic sum. Vulpis velox salit super canem pigerum in silva obscura. " + LatinProbe,
            ["lb"] = "Eng kloer Stëmm schwätzt roueg, wärend de Wand duerch d’Beem bléist an d’Villercher sangen. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, va, sa, za, sch, j, r, l.",
            ["lfn"] = "Un vose clara parla lentemente cuando la venta move la folias e la aves canta. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ca, ga; fa, va, sa, za, xa, ja, ra, la.",
            ["lg"] = "Olukyuse, ffe tunyumirwa okusoma ebitabo mu lulimi lwaffe buli lunaku. " + LatinProbe,
            ["lij"] = "Ina voxe ciæa a parla cianin mentre o vento o passa tra i erboi e l’ægoa a corre. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ca, ga; fa, va, sa, za, ra, la.",
            ["lt"] = "Įlinkdama fechtuotojo špaga sublykčiojo lūždama. Ąžuolas šlama, vėjas pučia per laukus. " + LatinProbe,
            ["ltg"] = "Skaidra bolss runoj mīreigi, kod viejs kustynoj kūku lopys. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, va, sa, za, ša, ža, ra, la.",
            ["lv"] = "Muļķa zirgs, kā tu nezini, ka čūskas nedzer kafiju un ēd tikai sēnes! Glāžšķūņa rūķīši dzēra čaju. " + LatinProbe,
            ["mi"] = "I rere te manu pango i runga i te rākau kōwhai, ā, ka waiata i tana waiata ātaahua. " + LatinProbe,
            ["mk"] = "Ѕидарскиот џин, кој ловеше ѓевреци, брзо скокна преку ќелавиот фокусник во четврток. " + CyrillicProbe,
            ["ml"] = "മാ, മി, മു, മേ, മോ; നാ, നി, നു, നേ, നോ; പാ, ബാ, താ, ദാ, ടാ, ഡാ, കാ, ഗാ; ഫാ, സാ, ശാ, ജാ, റാ, ലാ, യാ, വാ. തെളിഞ്ഞ ശബ്ദം പതുക്കെ പലതരം വാക്കുകൾ പറയുന്നു.",
            ["mn"] = "Цаг агаар сайхан байвал бид бүгдээрээ ууланд авирах болно. Хурдан хүрэн үнэг залхуу нохойг давж үсэрдэг. " + CyrillicProbe,
            ["mr"] = "क्षुब्ध ज्ञानी माणसाने झटपट आणि धैर्याने सर्व प्रश्नांची अचूक उत्तरे दिली. एक चतुर कोल्हा धावत आहे. " + DevanagariProbe,
            ["ms"] = "Aisyah sangat suka makan buah duku dan ciku bersama rakan-rakannya di waktu petang. " + LatinProbe,
            ["mt"] = "Leħen ċar jitkellem bil-mod waqt li r-riħ jgħaddi bejn is-siġar u l-għasafar ikantaw. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, va, sa, za, xa, ġa, ra, la.",
            ["mto"] = "Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, va, sa, za; tsa, dza, cha, ja; ra, la, ya, wa. Mami nanu paba tada kaga fava sasa rara lala.",
            ["my"] = "မာ၊ မိ၊ မု၊ မေ၊ မို။ နာ၊ နိ၊ နု၊ နေ၊ နို။ ပါ၊ ဘာ၊ တာ၊ ဒါ၊ ကာ၊ ဂါ။ ဖာ၊ ဆာ၊ ဇာ၊ ရာ၊ လာ၊ ယာ၊ ဝါ။ ကြည်လင်သောအသံသည် ဖြည်းဖြည်းနှင့် သဘာဝကျကျ စကားလုံးမျိုးစုံကို ပြောသည်။",
            ["nb"] = "Vår særnorske guttøks slår ned på den fete, jålete og late zombien. Kjære Yngve kjøpte ferskt brød, blåbær, røkt laks og grønne epler før toget gikk. " + LatinProbe,
            ["nci"] = "In cualli tlahtolli caquisti chipahuac ihuan yolcatl, ihcuac ehecatl quimolinia cuahuitl. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ta, ka, tla, tsa, cha, ya, wa, ra, la.",
            ["ne"] = "मा, मि, मु, मे, मो; ना, नि, नु, ने, नो; पा, बा, ता, दा, टा, डा, का, गा; फा, सा, शा, जा, रा, ला, या, वा. स्पष्ट आवाजले बिस्तारै विभिन्न शब्दहरू प्राकृतिक लयमा बोल्छ।",
            ["nl"] = "Pa's wijze lynx bezag vroom het fikse aquaduct. Zes cd-roms bevatten quizvragen. " + LatinProbe,
            ["nn"] = "Bære god kårkjekk Nynorsk-øks ut. Johan, ærleg og snill, prøvde å hjelpe den dårlege zombien. " + LatinProbe,
            ["nog"] = "Ма, ми, му; на, ни, ну; па, ба, та, да, ка, га; фа, ва, са, за; ша, жа, ча, ра, ла, йа, уа. Ашык авыс акрын айтылады, ел далада жай эседи.",
            ["om"] = "Sagaleen qulqulluun suuta dubbata, yeroo bubbeen muka keessaa darbu fi simbirroon sirbu. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, sa, za, sha, ja, ra, la.",
            ["or"] = "ମା, ମି, ମୁ, ମେ, ମୋ; ନା, ନି, ନୁ, ନେ, ନୋ; ପା, ବା, ତା, ଦା, ଟା, ଡା, କା, ଗା; ଫା, ସା, ଶା, ଜା, ରା, ଲା, ଯା, ୱା। ସ୍ପଷ୍ଟ ସ୍ୱର ଧୀରେ ଧୀରେ ବିଭିନ୍ନ ଶବ୍ଦ କହେ।",
            ["pa"] = "ਇੱਕ ਤੇਜ਼ ਭੂਰੀ ਲੂੰਬੜੀ ਆਲਸੀ ਕੁੱਤੇ ਦੇ ਉੱਪਰ ਛਾਲ ਮਾਰਦੀ ਹੈ। ਪੰਛੀ ਅਸਮਾਨ ਵਿੱਚ ਉੱਡ ਰਹੇ ਹਨ। " + GurmukhiProbe,
            ["pap"] = "Un bos kla ta papia poko poko mientras bientu ta pasa den palunan. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, va, sa, za, cha, ja, ra, la.",
            ["piqd"] = "Qapla'! tlhIngan Hol jatlhwI' QaQ batlh ghaj. HoSghaj tlhIngan QIchDaq q, Q, tlh, ch, gh, ng, H, j, r, l, m, n, w, y je Qoylu'.",
            ["pl"] = "Pchnąć w tę łódź jeża lub ośm skrzyń fig. Pójdźże, kiń tę chmurność w głąb flaszy. " + LatinProbe,
            ["ps"] = "روښانه غږ ورو او په طبيعي ډول بېلابېلې کلمې وايي، کله چې باد د ونو تر منځ تېرېږي. ما، مي، مو؛ نا، ني، نو؛ پا، با، تا، دا، کا، گا؛ فا، سا، زا، شا، را، لا، يا، وا.",
            ["pt"] = "Zebras caolhas de Java querem mandar saxofone para vovô. Um pequeno jabuti xingou o quati. " + LatinProbe,
            ["py"] = "Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, va, sa, za; sha, zha, cha, ja; ra, la, ya, wa. Mami nanu paba tada kaga fava sasa rara lala.",
            ["qdb"] = "Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, va, sa, za; sha, zha, cha, ja; ra, la, ya, wa. Mami nanu paba tada kaga fava sasa rara lala.",
            ["qu"] = "Huk k'anchayuq rimayqa sumaqta, allinta, pisi pisita rimachkan, wayra sachakunapi purichkaptin. Ma, mi, mu; na, ni, nu; pa, ta, ka, qa; cha, sha, ra, la, ya, wa.",
            ["quc"] = "Jun ch’ajch’oj ch’ab’äl nich’aw eqal, are taq ri kaqiq’ nik’ax pa taq che’. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; cha, ja, ra, la, ya, wa.",
            ["qya"] = "Elen síla lúmenn’ omentielvo. Nai elen siluva lyenna ar nai i cala nauva lyen. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ta, ca, qu, fa, va, sa, ra, la, ya.",
            ["ro"] = "Ghiță, prinde câinele vulpoiului săritor în tufiș, exclamând: «Auzi, bre, fă-o!» " + LatinProbe,
            ["ru"] = "Съешь же ещё этих мягких французских булок, да выпей чаю. Широкая электрификация южных губерний. " + CyrillicProbe,
            ["rup"] = "Unã boatsi limbidã zburã lin, cãndu vintulu treashi printre arbori. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ca, ga; fa, va, sa, za, sh, j, ra, la.",
            ["sd"] = "صاف آواز آهستي ۽ قدرتي لهجي سان مختلف لفظ چوي ٿو، جڏهن هوا وڻن مان گذري ٿي. ما، مي، مو؛ نا، ني، نو؛ پا، با، تا، دا، ڪا، گا؛ فا، سا، زا، شا، را، لا، يا، وا.",
            ["shn"] = "အသံၸႅင်ႈၸႂ် လၢတ်ႈၵႂၢမ်းလွႆးလွႆး လႄႈ တဵမ်တဵမ်၊ မိူဝ်ႈလူမ်းၽတ်ႉၽၢၼ်ႇတူၼ်ႈမႆႉ။ မာ၊ မိ၊ မု၊ မေ၊ မို; နာ၊ နိ၊ နု၊ နေ၊ နို.",
            ["si"] = "මා, මි, මු, මේ, මෝ; නා, නි, නු, නේ, නෝ; පා, බා, තා, දා, කා, ගා; ෆා, සා, ශා, ජා, රා, ලා, යා, වා. පැහැදිලි හඬ සෙමින් සහ ස්වභාවික රිද්මයකින් විවිධ වචන කියයි.",
            ["sjn"] = "Mae govannen. A Elbereth Gilthoniel, silivren penna míriel o menel aglar elenath. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ca, ga; fa, va, sa, ra, la.",
            ["sk"] = "Kŕdeľ šťastných ďatľov učí pri ústí Váhu mĺkveho koňa obhrýzať kôru a žrať väčšie lístie. " + LatinProbe,
            ["sl"] = "V kožuhu hudobnega fanta stopa rjavi mrož, ki želi spoznati vse žabe in ptice. " + LatinProbe,
            ["smj"] = "Tjoahkkes jiena ságastallá ráfes, gå biegga muorajt milta manná. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, va, sa, za, sj, tj, ra, la.",
            ["sn"] = "Mukomana mudiki akamhanya achienda kumba kunodya sadza rine nyama ne muriwo. " + LatinProbe,
            ["so"] = "Wiil yar oo geesi ah ayaa orday si uu u badbaadiyo shimbirta dhaawacan ee ku dhacday geedka. " + LatinProbe,
            ["sq"] = "Zogu i zi fluturoi në qiellin e kaltër. Vashëza çapkëne luante me një qen të vogël. " + LatinProbe,
            ["sr"] = "Љубазни џин Феђа ђипну преко шароликог жбуња. Брза смеђа лисица прескаче преко лењог пса. " + CyrillicProbe,
            ["st"] = "Moshanyana o monyane o ile a matha ho ya lapeng ho ya ja bohobe le nama e monate. " + LatinProbe,
            ["sv"] = "Flygande bäckasiner söka hwila på mjuka tuvor. Yxskaft, väg, zon, quist, jukebox. " + LatinProbe,
            ["sw"] = "Mvulana mdogo alikimbia nyumbani kula chakula kitamu alichopikiwa na mama yake mpendwa. " + LatinProbe,
            ["ta"] = "ஒரு விரைவான பழுப்பு நரி சோம்பேறி நாயின் மீது குதிக்கிறது. பறவைகள் வானத்தில் அழகாக பறக்கின்றன. " + TamilProbe,
            ["te"] = "ఒక వేగవంతమైన గోధుమ నక్క బద్ధకమైన కుక్క మీదుగా దూకుతుంది. ఈ రోజు చాలా అందంగా ఉంది. " + TeluguProbe,
            ["th"] = "เป็นมนุษย์สุดประเสริฐเลิศคุณค่า กว่าบรรดาฝูงสัตว์เดรัจฉาน จงสู้ฟันฝ่าอุปสรรคทั้งปวง " + ThaiProbe,
            ["ti"] = "ማ ሚ ሙ ሜ ሞ፣ ና ኒ ኑ ኔ ኖ፣ ፓ ባ ታ ዳ ካ ጋ፣ ፋ ቫ ሳ ዛ ሻ ጃ፣ ራ ላ ያ ዋ። ጽሩይ ድምጺ ቀስ ኢሉ ብተፈጥሯዊ ምት ዝተፈላለዩ ቃላት ይዛረብ።",
            ["tk"] = "Açyk ses haýal we tebigy äheňde dürli sözleri aýdýar, ýel agaçlaryň arasyndan geçýär. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, wa, sa, za, şa, ža, ra, la.",
            ["tl"] = "Ang mabilis na kayumangging asong gubat ay tumalon sa ibabaw ng tamad na aso sa kagubatan. " + LatinProbe,
            ["tn"] = "Mosimanyana o monnye o ne a tabogela kwa gae go ya go ja dijo tse di monate tsa mmaagwe. " + LatinProbe,
            ["tr"] = "Pijamalı hasta, yağız şoföre çabucak güvendi. Özgür çocuk küçük gölde yüzüp çiçekçiyle konuşurken güneşli gökyüzüne baktı. " + LatinProbe,
            ["ts"] = "Mufana lontsongo u tsutsumile a ya kaya ku ya dya swakudya swo nandziha swinene. " + LatinProbe,
            ["tt"] = "Ма, ми, му; на, ни, ну; па, ба, та, да, ка, га; фа, ва, са, за; ша, жа, ча, ра, ла, йа, уа. Ачык тавыш акрын һәм табигый ритм белән төрле сүзләр әйтә.",
            ["ug"] = "سۈزۈك ئاۋاز ئاستا ۋە تەبىئىي رېتىمدا ھەر خىل سۆزلەرنى ئېيتىدۇ، شامال دەرەخلەر ئارىسىدىن ئۆتىدۇ. ما، مى، مۇ؛ نا، نى، نۇ؛ پا، با، تا، دا، كا، گا؛ فا، سا، زا، شا، را، لا، يا، ۋا.",
            ["uk"] = "Чуєш їх, доцю, га? Кумедна ж ти, прощайся без ґольфів! Жебракують філософи при ґанку церкви в Гадячі. " + CyrillicProbe,
            ["ur"] = "ایک تیز بھوری لومڑی سست کتے کے اوپر سے چھلانگ لگاتی ہے۔ آسمان میں بادل چھائے ہوئے ہیں۔ " + ArabicScriptProbe,
            ["uz"] = "Tiniq ovoz sekin va tabiiy ohangda turli so‘zlarni aytadi, shamol daraxtlar orasidan o‘tadi. Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, va, sa, za, sha, ja, ra, la.",
            ["vi"] = "Con cáo nâu nhanh nhẹn nhảy qua con chó lười biếng trong rừng sâu, ánh nắng chiếu rọi. " + LatinProbe,
            ["xex"] = "Ma, me, mi, mo, mu; na, ne, ni, no, nu; pa, ba, ta, da, ka, ga; fa, va, sa, za; sha, zha, cha, ja; ra, la, ya, wa. Mami nanu paba tada kaga fava sasa rara lala.",
            ["xh"] = "Inkwenkwezi encinci ibaleka ukuya kutyha ukutya okumnandi ekhaya nabahlobo bayo. " + LatinProbe,
            ["yo"] = "Ọmọkùnrin kékeré náà sáré lọ sí ilé láti jẹ oúnjẹ dídùn tí ìyá rẹ̀ sè fún un. " + LatinProbe,
            ["yue"] = "今日天氣幾好，朝早有微風吹過樹林，細路喺橋邊講嘢同唱歌。山、水、風、花、鳥、月、雨，聲音高低長短慢慢變化。",
            ["yue-latn-jyutping"] = "maa1 maa2 maa3 maa4 maa5 maa6, baa1 baa2 baa3 baa4 baa5 baa6, gaa1 gaa2 gaa3 gaa4 gaa5 gaa6. gam1jat6 tin1hei3 hou2, sai3lou6 hai2 kiu4bin1 gong2je5 tung4 coeng3go1.",
            ["zu"] = "Umfana omncane ugijime waya ekhaya wazodla ukudla okumnandi kakhulu asiphiwe umama wakhe. " + LatinProbe,
        };

        // Current eSpeak-ng accents/dialects that can share the same reference wording.
        // The configured voice code remains active during phonemization, so sharing text does
        // not collapse the actual accent/dialect behavior of eSpeak.
        AddAliases(texts, "ca", "ca-ba", "ca-nw", "ca-va");
        AddAliases(texts, "en", "en-029", "en-gb-scotland", "en-gb-x-gbclan", "en-gb-x-gbcwmd", "en-gb-x-rp", "en-us", "en-us-nyc");
        AddAliases(texts, "es", "es-419");
        AddAliases(texts, "fr", "fr-be", "fr-ch");
        AddAliases(texts, "mn", "mn-f");
        AddAliases(texts, "ps", "ps-x-yusufzai", "ps-x-northwest", "ps-x-southeast");
        AddAliases(texts, "pt", "pt-br");
        AddAliases(texts, "ru", "ru-cl", "ru-lv");
        AddAliases(texts, "vi", "vi-vn-x-central", "vi-vn-x-south");
        AddAliases(texts, "cmn", "zh", "zh-cn", "zh-sg");
        AddAliases(texts, "nb", "no");
        AddAliases(texts, "piqd", "tlh");

        return texts;
    }

    private static void AddAliases(
        Dictionary<string, string> texts,
        string sourceCode,
        params string[] aliases)
    {
        if (!texts.TryGetValue(sourceCode, out string? referenceText))
        {
            return;
        }

        foreach (string alias in aliases)
        {
            texts[alias] = referenceText;
        }
    }

    private const string FallbackText =
        "The quick brown fox jumps over the lazy dog while a calm speaker reads several varied phrases. " +
        LatinProbe;

    public void GenerateAndCacheBaseFingerprint()
    {
        string configuredVoice = _piperConfig.Espeak.Voice ?? "en";
        string normalizedVoice = NormalizeVoiceCode(configuredVoice);
        string textToSpeak = ResolveReferenceText(normalizedVoice, out string resolvedCode);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[AUTO-BASE] Generating baseline footprint for model language: '{configuredVoice}'...");

        if (resolvedCode == "fallback")
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                $"[AUTO-BASE] No reference profile found for '{normalizedVoice}'. " +
                "Using the phonetically varied fallback passage.");
            Console.ForegroundColor = ConsoleColor.Cyan;
        }
        else if (!string.Equals(normalizedVoice, resolvedCode, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                $"[AUTO-BASE] Using reference profile '{resolvedCode}' for '{normalizedVoice}'.");
        }
        else
        {
            Console.WriteLine($"[AUTO-BASE] Using phonetically diverse reference for '{resolvedCode}'.");
        }

        if (string.Equals(resolvedCode, "chr", StringComparison.OrdinalIgnoreCase))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                "[AUTO-BASE] Cherokee uses eSpeak-ng's experimental DF frontend; " +
                "baseline pronunciation quality may vary.");
            Console.ForegroundColor = ConsoleColor.Cyan;
        }

        // 1. Phonemize the reference text and synthesize the raw audio using the loaded base Piper model.
        string phonemes = _phonemizer.GetPhonemes(textToSpeak);
        byte[] baseAudioBytes = _piperRunner.SynthesizeAudio(phonemes, false, true, 1.0f);

        // 2. Read the generated Piper WAV into memory.
        using var ms = new MemoryStream(baseAudioBytes);
        using var reader = new WaveFileReader(ms);

        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm ||
            reader.WaveFormat.BitsPerSample != 16 ||
            reader.WaveFormat.Channels != 1)
        {
            throw new InvalidDataException(
                $"Piper base reference must be mono 16-bit PCM, got {reader.WaveFormat}.");
        }

        double referenceSeconds = reader.TotalTime.TotalSeconds;
        Console.WriteLine($"[AUTO-BASE] Reference audio duration: {referenceSeconds:F2} s.");

        if (referenceSeconds < RecommendedMinimumReferenceSeconds)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                $"[AUTO-BASE] Reference is shorter than Tsubaki's " +
                $"{RecommendedMinimumReferenceSeconds:F0} s quality target. " +
                "Tone embedding may be less representative.");
            Console.ForegroundColor = ConsoleColor.Cyan;
        }

        // 3. Convert the complete mono PCM stream to float samples.
        var provider = reader.ToSampleProvider();
        int sampleCapacity = checked((int)(reader.Length / sizeof(short)));
        var samples = new float[sampleCapacity];
        int samplesRead = 0;

        while (samplesRead < samples.Length)
        {
            int read = provider.Read(samples, samplesRead, samples.Length - samplesRead);
            if (read <= 0) break;

            samplesRead += read;
        }

        if (samplesRead <= 0)
            throw new InvalidDataException("Piper base reference contains no PCM samples.");

        // 4. Resample from the actual WAV rate to the OpenVoice extractor rate.
        int openVoiceRate = _openVoice.GetTargetSamplingRate();
        var resampled = _audioProcessor.Resample(
            samples,
            samplesRead,
            reader.WaveFormat.SampleRate,
            openVoiceRate);

        try
        {
            // Match reference WAV normalization so base and target embeddings are extracted
            // under equivalent loudness conditions.
            _audioProcessor.NormalizeLufs(
                resampled.Buffer.AsSpan(0, resampled.Length),
                openVoiceRate,
                _clonerConfig.ReferenceAudioTargetLufs);

            // 5. Generate the magnitude spectrogram.
            var spec = _audioProcessor.GetMagnitudeSpectrogram(
                resampled.Buffer.AsSpan(0, resampled.Length));

            // 6. Extract and cache the base Piper tone-color embedding.
            var baseFingerprint = _openVoice.ExtractToneColor(spec);
            _openVoice.VoiceLibrary["piper_base"] = baseFingerprint;
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(resampled.Buffer);
        }

        Console.WriteLine("[AUTO-BASE] Dynamic base footprint successfully calculated and stored in memory.");
        Console.ResetColor();
    }

    private static string ResolveReferenceText(
        string normalizedVoice,
        out string resolvedCode)
    {
        if (_referenceTexts.TryGetValue(normalizedVoice, out string? exact))
        {
            resolvedCode = normalizedVoice;
            return exact;
        }

        // Unknown regional/private-use variants progressively fall back through their BCP-47
        // parents (for example en-GB-x-custom -> en-GB -> en) instead of immediately discarding
        // every subtag as the previous Split('-')[0] implementation did.
        string candidate = normalizedVoice;

        while (true)
        {
            int separator = candidate.LastIndexOf('-');
            if (separator <= 0)
            {
                break;
            }

            candidate = candidate[..separator];

            if (_referenceTexts.TryGetValue(candidate, out string? inherited))
            {
                resolvedCode = candidate;
                return inherited;
            }
        }

        resolvedCode = "fallback";
        return FallbackText;
    }

    private static string NormalizeVoiceCode(string voiceCode)
    {
        string normalized = voiceCode
            .Trim()
            .Replace('_', '-')
            .ToLowerInvariant();

        return string.IsNullOrWhiteSpace(normalized)
            ? "en"
            : normalized;
    }
}