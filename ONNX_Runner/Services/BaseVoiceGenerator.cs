using NAudio.Wave;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

public class BaseVoiceGenerator(
    UnifiedPhonemizer phonemizer,
    PiperRunner piperRunner,
    AudioProcessor audioProcessor,
    OpenVoiceRunner openVoice,
    PiperConfig piperConfig)
{
    private readonly UnifiedPhonemizer _phonemizer = phonemizer;
    private readonly PiperRunner _piperRunner = piperRunner;
    private readonly AudioProcessor _audioProcessor = audioProcessor;
    private readonly OpenVoiceRunner _openVoice = openVoice;
    private readonly PiperConfig _piperConfig = piperConfig;

    // Словник еталонних текстів (Панграми або фонетично багаті речення для 75 мов lingua-dotnet)
    private readonly Dictionary<string, string> _referenceTexts = new(StringComparer.OrdinalIgnoreCase)
    {
        // Afrikaans
        { "af", "Piet smyt vyf skuins bokse vir die kwikstertjie. Jakkals sjampanje wals zyl." },
        // Albanian
        { "sq", "Zogu i zi fluturoi në qiellin e kaltër. Vashëza çapkëne luante me një qen të vogël." },
        // Arabic
        { "ar", "نص حكيم له سر قاطع وذو شأن عظيم مكتوب على ثوب أخضر ومغلف بجلد أزرق." },
        // Armenian
        { "hy", "Ֆիզիկոս Գևորգը շուտով կգտնի ճշգրիտ ելքը, որը կօգնի լուծել այս բարդ խնդիրը։" },
        // Azerbaijani
        { "az", "Zəfər, cəsur qəhrəman, şadlıqla və xüsusi coşqu ilə fəxr etdi. Böyük jürinin rəyi qətiyyətlidir." },
        // Basque
        { "eu", "Zebra, azeri, txakur eta katu bat elkarrekin joan ziren mendira, poz-pozik abesten." },
        // Belarusian
        { "be", "У Іўі худы жвавы чорт у зялёнай камізэльцы пабег пад елку. Шчупак дзяўбнуў кручок." },
        // Bengali
        { "bn", "বিড়ালটা হঠাৎ লাফিয়ে উঠে সাদা ইঁদুরটিকে ধরে ফেলল। মেঘলা দিনে বৃষ্টি শুরু হলো।" },
        // Bosnian
        { "bs", "Fin džip, gluh jež i žut crv skučiše se na dnu. Ljubičasti slon brzo trči." },
        // Bulgarian
        { "bg", "Жълтата дюля беше щастлива, че пухкавият зайчо ѝ подари цвете. Шофьорът търси път." },
        // Catalan
        { "ca", "Jove xef, porti whisky amb quinze glaçons d'hidrogen, coi! La cigonya viatja al sud." },
        // Chinese
        { "zh", "敏捷的棕色狐狸跳过懒惰的狗。天空是蓝色的，水是清澈的，微风轻抚着树叶。" },
        // Croatian
        { "hr", "Gojazni đačić s leptirićem demonstrirao je pejsažni crtež. Ljubičasta žaba leti." },
        // Czech
        { "cs", "Příliš žluťoučký kůň úpěl ďábelské ódy. Hleď, toť stín, jak šíp běží podél stěny!" },
        // Danish
        { "da", "Quizdeltagerne spiste jordbær med fløde, mens cirkusklovnen Walther spillede på xylofon." },
        // Dutch
        { "nl", "Pa's wijze lynx bezag vroom het fikse aquaduct. Zes cd-roms bevatten quizvragen." },
        // English
        { "en", "The quick brown fox jumps over the lazy dog. A wizard's job is to vex chumps quickly in fog." },
        // Esperanto
        { "eo", "Eĥoŝanĝo ĉiuĵaŭde. Laŭ ebleco, ni provu uzi ĉiujn literojn de la alfabeto." },
        // Estonian
        { "et", "Põdur Zagrebi tšellomängija-följetonist Ciqku jääb haigeks, väljub žüriist. Jõgi voolab." },
        // Finnish
        { "fi", "Törkylempijävongahdus. Charles Darwin joi viskiä ja konjakkia fagottia soittaessaan." },
        // French
        { "fr", "Portez ce vieux whisky au juge blond qui fume. Voix ambiguë d'un cœur qui au zéphyr préfère les jattes de kiwis." },
        // Ganda
        { "lg", "Olukyuse, ffe tunyumirwa okusoma ebitabo mu lulimi lwaffe buli lunaku." },
        // Georgian
        { "ka", "ჩვეულებრივი, სწრაფი მელა გადახტა ზარმაცი ძაღლის თავზე. მზის სხივები ანათებს მთებს." },
        // German
        { "de", "Falsches Üben von Xylophonmusik quält jeden größeren Zwerg. Victor jagt zwölf Boxkämpfer." },
        // Greek
        { "el", "Ταχίστη αλώπηξ βαφής ψημένη γη, δρασκελίζει υπέρ νωθρού κυνός. Το παιδί παίζει." },
        // Gujarati
        { "gu", "ઝડપી ભૂરો શિયાળ આળસુ કૂતરા પર કૂદે છે. પક્ષીઓ આકાશમાં સુંદર ગીતો ગાય છે." },
        // Hebrew
        { "he", "דג סקרן שט בים זך אך לפתע פגש חבורה נחמדה שצצה כך. איזה יום יפה היום." },
        // Hindi
        { "hi", "ऋषियों को सताने वाले दुष्ट राक्षसों के मरण पर देवता भी खुश थे। तेज़ भूरी लोमड़ी आलसी कुत्ते पर कूदती है।" },
        // Hungarian
        { "hu", "Árvíztűrő tükörfúrógép. Egy hűtlen vejét fülöncsípő, dühös mexikói spanyol viador." },
        // Icelandic
        { "is", "Kæmi ný öxi hér ykist þjófum nú bæði víl og ádrepa. Vaðlaheiðarvegavinnuverkfærageymsluskúraútidyralyklakippuhringur." },
        // Indonesian
        { "id", "Jerapah belang yang lucu berjalan zigzag di taman safari xanadu bersama kawanannya." },
        // Irish
        { "ga", "D'ith an frog beag buí an chuileog mhór dhubh go tapa. D'fhéach an cat go géar air." },
        // Italian
        { "it", "Pranzo d'acqua fa volti sghembi. Ma la volpe col suo balzo ha raggiunto il quieto Fido." },
        // Japanese
        { "ja", "いろはにほへとちりぬるを わかよたれそつねならむ うゐのおくやまけふこえて あさきゆめみしゑひもせす。" },
        // Kazakh
        { "kk", "Барлық адамдар тумысынан азат және қадір-қасиеті мен құқықтары тең болып дүниеге келеді." },
        // Korean
        { "ko", "다람쥐 헌 쳇바퀴에 타고파. 키스의 고유조건은 입술끼리 만나야 하고 특별한 기술은 필요치 않다." },
        // Latin
        { "la", "Sic fui, non sum; non fui, sic sum. Vulpis velox salit super canem pigerum in silva obscura." },
        // Latvian
        { "lv", "Muļķa zirgs, kā tu nezini, ka čūskas nedzer kafiju un ēd tikai sēnes! Glāžšķūņa rūķīši dzēra čaju." },
        // Lithuanian
        { "lt", "Įlinkdama fechtuotojo špaga sublykčiojo lūždama. Ąžuolas šlama, vėjas pučia per laukus." },
        // Macedonian
        { "mk", "Ѕидарскиот џин, кој ловеше ѓевреци, брзо скокна преку ќелавиот фокусник во четврток." },
        // Malay
        { "ms", "Aisyah sangat suka makan buah duku dan ciku bersama rakan-rakannya di waktu petang." },
        // Maori
        { "mi", "I rere te manu pango i runga i te rākau kōwhai, ā, ka waiata i tana waiata ātaahua." },
        // Marathi
        { "mr", "क्षुब्ध ज्ञानी माणसाने झटपट आणि धैर्याने सर्व प्रश्नांची अचूक उत्तरे दिली. एक चतुर कोल्हा धावत आहे." },
        // Mongolian
        { "mn", "Цаг агаар сайхан байвал бид бүгдээрээ ууланд авирах болно. Хурдан хүрэн үнэг залхуу нохойг давж үсэрдэг." },
        // Norwegian Nynorsk
        { "nn", "Bære god kårkjekk Nynorsk-øks ut. Johan, ærleg og snill, prøvde å hjelpe den dårlege zombien." },
        // Norwegian Bokmal (NB)
        { "nb", "Vår særnorske guttøks slår ned på den fete, jålete og late zombien. C, Q, W og X er også med." },
        // Persian
        { "fa", "پیمان با یک گنجشک کوچک در خیابان ظفر قدم می‌زد و آواز می‌خواند. روباه قهوه‌ای سریع می‌پرد." },
        // Polish
        { "pl", "Pchnąć w tę łódź jeża lub ośm skrzyń fig. Pójdźże, kiń tę chmurność w głąb flaszy." },
        // Portuguese
        { "pt", "Zebras caolhas de Java querem mandar saxofone para vovô. Um pequeno jabuti xingou o quati." },
        // Punjabi
        { "pa", "ਇੱਕ ਤੇਜ਼ ਭੂਰੀ ਲੂੰਬੜੀ ਆਲਸੀ ਕੁੱਤੇ ਦੇ ਉੱਪਰ ਛਾਲ ਮਾਰਦੀ ਹੈ। ਪੰਛੀ ਅਸਮਾਨ ਵਿੱਚ ਉੱਡ ਰਹੇ ਹਨ।" },
        // Romanian
        { "ro", "Ghiță, prinde câinele vulpoiului săritor în tufiș, exclamând: «Auzi, bre, fă-o!»" },
        // Russian
        { "ru", "Съешь же ещё этих мягких французских булок, да выпей чаю. Широкая электрификация южных губерний." },
        // Serbian
        { "sr", "Љубазни џин Феђа ђипну преко шароликог жбуња. Брза смеђа лисица прескаче преко лењог пса." },
        // Shona
        { "sn", "Mukomana mudiki akamhanya achienda kumba kunodya sadza rine nyama ne muriwo." },
        // Slovak
        { "sk", "Kŕdeľ šťastných ďatľov učí pri ústí Váhu mĺkveho koňa obhrýzať kôru a žrať väčšie lístie." },
        // Slovenian
        { "sl", "V kožuhu hudobnega fanta stopa rjavi mrož, ki želi spoznati vse žabe in ptice." },
        // Somali
        { "so", "Wiil yar oo geesi ah ayaa orday si uu u badbaadiyo shimbirta dhaawacan ee ku dhacday geedka." },
        // Sotho
        { "st", "Moshanyana o monyane o ile a matha ho ya lapeng ho ya ja bohobe le nama e monate." },
        // Spanish
        { "es", "El pingüino Wenceslao hizo kilómetros bajo exhaustiva lluvia y frío, añoraba a su querido cachorro." },
        // Swahili
        { "sw", "Mvulana mdogo alikimbia nyumbani kula chakula kitamu alichopikiwa na mama yake mpendwa." },
        // Swedish
        { "sv", "Flygande bäckasiner söka hwila på mjuka tuvor. Yxskaft, väg, zon, quist, jukebox." },
        // Tagalog
        { "tl", "Ang mabilis na kayumangging asong gubat ay tumalon sa ibabaw ng tamad na aso sa kagubatan." },
        // Tamil
        { "ta", "ஒரு விரைவான பழுப்பு நரி சோம்பேறி நாயின் மீது குதிக்கிறது. பறவைகள் வானத்தில் அழகாக பறக்கின்றன." },
        // Telugu
        { "te", "ఒక వేగవంతమైన గోధుమ నక్క బద్ధకమైన కుక్క మీదుగా దూకుతుంది. ఈ రోజు చాలా అందంగా ఉంది." },
        // Thai
        { "th", "เป็นมนุษย์สุดประเสริฐเลิศคุณค่า กว่าบรรดาฝูงสัตว์เดรัจฉาน จงสู้ฟันฝ่าอุปสรรคทั้งปวง" },
        // Tsonga
        { "ts", "Mufana lontsongo u tsutsumile a ya kaya ku ya dya swakudya swo nandziha swinene." },
        // Tswana
        { "tn", "Mosimanyana o monnye o ne a tabogela kwa gae go ya go ja dijo tse di monate tsa mmaagwe." },
        // Turkish
        { "tr", "Pijamalı hasta, yağız şoföre çabucak güvendi. ABCÇDEFGĞHIİJKLMNOÖPRSŞTUÜVYZ." },
        // Ukrainian
        { "uk", "Чуєш їх, доцю, га? Кумедна ж ти, прощайся без ґольфів! Жебракують філософи при ґанку церкви в Гадячі." },
        // Urdu
        { "ur", "ایک تیز بھوری لومڑی سست کتے کے اوپر سے چھلانگ لگاتی ہے۔ آسمان میں بادل چھائے ہوئے ہیں۔" },
        // Vietnamese
        { "vi", "Con cáo nâu nhanh nhẹn nhảy qua con chó lười biếng trong rừng sâu, ánh nắng chiếu rọi." },
        // Welsh
        { "cy", "Parciai ffenics blin gymaint â charlwm dew, dylluan fud, a rhinoseros gwyllt." },
        // Xhosa
        { "xh", "Inkwenkwezi encinci ibaleka ukuya kutyha ukutya okumnandi ekhaya nabahlobo bayo." },
        // Yoruba
        { "yo", "Ọmọkùnrin kékeré náà sáré lọ sí ilé láti jẹ oúnjẹ dídùn tí ìyá rẹ̀ sè fún un." },
        // Zulu
        { "zu", "Umfana omncane ugijime waya ekhaya wazodla ukudla okumnandi kakhulu asiphiwe umama wakhe." }
    };

    // Фолбек: просто англійський алфавіт через кому
    private const string FallbackText = "A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z.";

    public void GenerateAndCacheBaseFingerprint()
    {
        // Витягуємо базовий код мови (наприклад, з "en-us" беремо "en")
        string langCode = _piperConfig.Espeak.Voice?.Split('-')[0].ToLower() ?? "en";

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[AUTO-BASE] Generating baseline footprint for model language: '{langCode}'...");

        if (!_referenceTexts.TryGetValue(langCode, out string? textToSpeak))
        {
            Console.WriteLine($"[AUTO-BASE] Exact match not found for '{langCode}'. Using alphabet fallback.");

            // Тут ми одразу перепризначаємо null на наш безпечний FallbackText
            textToSpeak = FallbackText;
        }
        else
        {
            Console.WriteLine($"[AUTO-BASE] Using native phonetically rich sentences for '{langCode}'.");
        }

        // Отримуємо фонеми та генеруємо аудіо (базовий голос Piper)
        string phonemes = _phonemizer.GetPhonemes(textToSpeak);
        byte[] baseAudioBytes = _piperRunner.SynthesizeAudio(phonemes, 1.0f);

        // Читаємо в float[] через NAudio прямо в оперативній пам'яті
        using var ms = new MemoryStream(baseAudioBytes);
        using var reader = new WaveFileReader(ms);

        // Перетворюємо 16-bit PCM у float через SampleProvider
        var provider = reader.ToSampleProvider();

        // 16 біт = 2 байти на семпл
        var samples = new float[reader.Length / 2];
        provider.Read(samples, 0, samples.Length);

        // Робимо спектрограму і витягуємо зліпок кольору голосу
        var spec = _audioProcessor.GetMagnitudeSpectrogram(samples);
        var baseFingerprint = _openVoice.ExtractToneColor(spec);

        // Зберігаємо в пам'ять під ключем "piper_base"
        _openVoice.VoiceLibrary["piper_base"] = baseFingerprint;

        Console.WriteLine("[AUTO-BASE] Dynamic base footprint successfully calculated and stored in memory.");
        Console.ResetColor();
    }
}