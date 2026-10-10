using ONNX_Runner.Models;
using ONNX_Runner.Services;

// Regression corpus: explicit expected boundaries, never calculated from TextChunker itself.
// Inherently ambiguous uppercase initials use conservative keep-together expectations.
// Other categories exercise generic syntax rules independently of known abbreviation lists.
var chunker = new TextChunker(new ChunkerSettings { MaxChunkLength = 1000 });

var cases = new (string Category, string Name, string Input, string[] Expected)[]
{
    // Baseline
    ("Baseline", "English saint", "St. Peter spoke. Then he left.", ["St. Peter spoke.", "Then he left."]),
    ("Baseline", "Nordic saint", "St. Olav er kjent. Neste setning.", ["St. Olav er kjent.", "Neste setning."]),
    ("Baseline", "French saint", "Ste. Marie est arrivée. Ensuite elle repart.", ["Ste. Marie est arrivée.", "Ensuite elle repart."]),
    ("Baseline", "Spanish honorific", "Sr. García llegó. Luego salió.", ["Sr. García llegó.", "Luego salió."]),
    ("Baseline", "Spanish feminine title", "Dra. López llegó. Después salió.", ["Dra. López llegó.", "Después salió."]),
    ("Baseline", "Spanish lowercase title", "sr. García llegó. Luego salió.", ["sr. García llegó.", "Luego salió."]),
    ("Baseline", "Italian lowercase title", "dott. Rossi è arrivato. Poi partì.", ["dott. Rossi è arrivato.", "Poi partì."]),
    ("Baseline", "Polish lowercase doctor", "dr. Kowalski przybył. Potem wyszedł.", ["dr. Kowalski przybył.", "Potem wyszedł."]),
    ("Baseline", "Polish habilitation", "dr hab. Kowalski przybył. Koniec.", ["dr hab. Kowalski przybył.", "Koniec."]),
    ("Baseline", "Polish saint lowercase", "św. Jan jest tutaj. Koniec.", ["św. Jan jest tutaj.", "Koniec."]),
    ("Baseline", "Polish street", "ul. Długa jest długa. Koniec.", ["ul. Długa jest długa.", "Koniec."]),
    ("Baseline", "Polish avenue", "al. Jana Pawła II jest długa. Koniec.", ["al. Jana Pawła II jest długa.", "Koniec."]),
    ("Baseline", "Ukrainian street", "вул. Хрещатик відома. Далі текст.", ["вул. Хрещатик відома.", "Далі текст."]),
    ("Baseline", "Ukrainian named after", "парк ім. Шевченка відкрито. Далі.", ["парк ім. Шевченка відкрито.", "Далі."]),
    ("Baseline", "Ukrainian lowercase professor", "проф. Петренко пояснив. Далі.", ["проф. Петренко пояснив.", "Далі."]),
    ("Baseline", "Russian street", "ул. Пушкина рядом. Потом домой.", ["ул. Пушкина рядом.", "Потом домой."]),
    ("Baseline", "Russian mister", "г-н. Иванов пришёл. Потом ушёл.", ["г-н. Иванов пришёл.", "Потом ушёл."]),
    ("Baseline", "Thai doctor", "ดร. สมชายมาแล้ว. จบ.", ["ดร. สมชายมาแล้ว.", "จบ."]),
    ("Baseline", "Hebrew doctor", "דר. דוד הגיע. המשך.", ["דר. דוד הגיע.", "המשך."]),
    ("Baseline", "Vietnamese title", "TS. Nguyễn có mặt. Xong.", ["TS. Nguyễn có mặt.", "Xong."]),
    ("Baseline", "Norwegian example", "Vi besøkte f.eks. Oslo i går. Så dro vi hjem.", ["Vi besøkte f.eks. Oslo i går.", "Så dro vi hjem."]),
    ("Baseline", "Swedish example", "Vi såg t.ex. Stockholm först. Sedan åkte vi.", ["Vi såg t.ex. Stockholm först.", "Sedan åkte vi."]),
    ("Baseline", "Norwegian among others", "Det gjelder bl.a. Bergen og Oslo. Neste.", ["Det gjelder bl.a. Bergen og Oslo.", "Neste."]),
    ("Baseline", "English example", "Use e.g. Python here. Next step.", ["Use e.g. Python here.", "Next step."]),
    ("Baseline", "English explanation", "It means i.e. John is first. Then Jane.", ["It means i.e. John is first.", "Then Jane."]),
    ("Baseline", "German example", "Wir besuchen z.B. Berlin morgen. Danach fahren wir.", ["Wir besuchen z.B. Berlin morgen.", "Danach fahren wir."]),
    ("Baseline", "English versus", "The case is Smith vs. Jones. It ended.", ["The case is Smith vs. Jones.", "It ended."]),
    ("Baseline", "English number", "No. 12 is available. Next item.", ["No. 12 is available.", "Next item."]),
    ("Baseline", "Figure", "Fig. 2 shows it. Next page.", ["Fig. 2 shows it.", "Next page."]),
    ("Baseline", "Volume", "Vol. 3 is out. Read it.", ["Vol. 3 is out.", "Read it."]),
    ("Baseline", "Norwegian time", "Vi møtes kl. 10 i dag. Ha det.", ["Vi møtes kl. 10 i dag.", "Ha det."]),
    ("Baseline", "Approximation", "Det er ca. 20 minutter unna. Gå nå.", ["Det er ca. 20 minutter unna.", "Gå nå."]),
    ("Baseline", "Academic citation", "Smith et al. (2020) agree. Next study.", ["Smith et al. (2020) agree.", "Next study."]),
    ("Baseline", "Et al sentence ending", "It was Smith et al. Next study.", ["It was Smith et al.", "Next study."]),
    ("Baseline", "Etc sentence ending", "We covered this, etc. Next topic.", ["We covered this, etc.", "Next topic."]),
    ("Baseline", "Lowercase unit not honorific", "It took 3 ms. Capt. Jones agreed.", ["It took 3 ms.", "Capt. Jones agreed."]),
    ("Baseline", "Ordinary abbreviation can end sentence", "The last item is approx. Next section.", ["The last item is approx.", "Next section."]),
    ("Baseline", "Single letter initial", "A. Smith spoke. Done.", ["A. Smith spoke.", "Done."]),
    ("Baseline", "Dotted acronym", "The U.S. Army moved. Later it stopped.", ["The U.S. Army moved.", "Later it stopped."]),
    ("Baseline", "Decimal", "Value 3.14 is accepted. Done.", ["Value 3.14 is accepted.", "Done."]),
    ("Baseline", "Line boundary overrides title", "Dr.\nSmith arrived.", ["Dr.", "Smith arrived."]),
    ("Baseline", "Compatibility period", "St． Olav is known. Next.", ["St． Olav is known.", "Next."]),

    // Generic initials
    ("Generic initials", "Unknown Latin initial Q", "Q. Rivera spoke. Next.", ["Q. Rivera spoke.", "Next."]),
    ("Generic initials", "Unknown Latin initial V", "V. Andersson entered. Then he spoke.", ["V. Andersson entered.", "Then he spoke."]),
    ("Generic initials", "Middle initials three", "J. R. R. Tolkien wrote it. Readers agree.", ["J. R. R. Tolkien wrote it.", "Readers agree."]),
    ("Generic initials", "Middle initials two", "E. M. Forster wrote novels. Readers agree.", ["E. M. Forster wrote novels.", "Readers agree."]),
    ("Generic initials", "Cyrillic uppercase initial", "Ж. Левченко сказав. Далі текст.", ["Ж. Левченко сказав.", "Далі текст."]),
    ("Generic initials", "Cyrillic chained initials", "І. Я. Франко писав. Далі текст.", ["І. Я. Франко писав.", "Далі текст."]),
    ("Generic initials", "Greek initial", "Ω. Παπαδόπουλος μίλησε. Τέλος.", ["Ω. Παπαδόπουλος μίλησε.", "Τέλος."]),
    ("Generic initials", "Spanish initial", "N. García llegó. Después salió.", ["N. García llegó.", "Después salió."]),
    ("Generic initials", "Initial before number", "Option X. 5 is marked. Continue.", ["Option X. 5 is marked.", "Continue."]),
    ("Generic initials", "Lowercase initial before surname — lowercase boundary", "a. Brown appeared. Next.", ["a.", "Brown appeared.", "Next."]),
    ("Generic initials", "Initial followed by surname apostrophe", "O. O'Neill spoke. Next.", ["O. O'Neill spoke.", "Next."]),
    ("Generic initials", "Initials separated by many spaces", "R.   K.   Singh wrote. Later.", ["R.   K.   Singh wrote.", "Later."]),

    // Generic dotted
    ("Generic dotted", "Unknown dotted uppercase two parts", "Q.R. Laboratory approved it. Next.", ["Q.R. Laboratory approved it.", "Next."]),
    ("Generic dotted", "Unknown dotted uppercase three parts", "C.D.E. Institute opened. Later.", ["C.D.E. Institute opened.", "Later."]),
    ("Generic dotted", "Unknown dotted acronym Cyrillic", "З.С. Комітет засідав. Потім пішли.", ["З.С. Комітет засідав.", "Потім пішли."]),
    ("Generic dotted", "Unknown dotted acronym Greek", "Δ.Β. Ομάδα ήρθε. Τέλος.", ["Δ.Β. Ομάδα ήρθε.", "Τέλος."]),
    ("Generic dotted", "Unknown all-cap letters", "T.T.S. Engine runs now. Next.", ["T.T.S. Engine runs now.", "Next."]),
    ("Generic dotted", "Unknown six-part acronym", "A.B.C.D.E.F. Group met today. Next.", ["A.B.C.D.E.F. Group met today.", "Next."]),
    ("Generic dotted", "Unknown lowercase dotted abbreviation", "We used q.x. as a placeholder. Next.", ["We used q.x. as a placeholder.", "Next."]),
    ("Generic dotted", "Unknown three-part lowercase dotted abbreviation", "The note said a.b.c. before a noun. Next.", ["The note said a.b.c. before a noun.", "Next."]),
    ("Generic dotted", "Mixed-case dotted abbreviation before noun", "It says q.R. value is missing. Next.", ["It says q.R. value is missing.", "Next."]),
    ("Generic dotted", "Generic period followed lowercase", "They marked qzx. as shorthand. Next.", ["They marked qzx. as shorthand.", "Next."]),
    ("Generic dotted", "Unknown token not an abbreviation before uppercase", "The file says orbit. Now read more.", ["The file says orbit.", "Now read more."]),
    ("Generic dotted", "Unknown short dotted token before uppercase", "This is x.ai. Tomorrow we leave.", ["This is x.ai.", "Tomorrow we leave."]),
    ("Generic dotted", "Unknown lowercase term before uppercase", "The result was ok. Tomorrow we retry.", ["The result was ok.", "Tomorrow we retry."]),
    ("Generic dotted", "Known uppercase initialism with new lowercase clause", "The U.K. government issued a notice. Next.", ["The U.K. government issued a notice.", "Next."]),
    ("Generic dotted", "Dotted game title", "S.T.A.L.K.E.R. runs here. Next.", ["S.T.A.L.K.E.R. runs here.", "Next."]),
    ("Generic dotted", "Dotted uppercase initials with periods inside", "The Q.R.S. Committee met. They left.", ["The Q.R.S. Committee met.", "They left."]),

    // Titles and names
    ("Titles and names", "English reverend", "Rev. Thomas arrived. Everyone stood.", ["Rev. Thomas arrived.", "Everyone stood."]),
    ("Titles and names", "English military captain", "Capt. Smith spoke. Everyone listened.", ["Capt. Smith spoke.", "Everyone listened."]),
    ("Titles and names", "English military colonel", "Col. Davis gave orders. We left.", ["Col. Davis gave orders.", "We left."]),
    ("Titles and names", "English senator", "Sen. Johnson called. The meeting began.", ["Sen. Johnson called.", "The meeting began."]),
    ("Titles and names", "English minister", "Dr. Morgan opened the door. Prof. Smith replied.", ["Dr. Morgan opened the door.", "Prof. Smith replied."]),
    ("Titles and names", "English Ms capital", "Ms. Brown arrived. She smiled.", ["Ms. Brown arrived.", "She smiled."]),
    ("Titles and names", "English Mrs mixed case", "Mrs. Taylor waved. She left.", ["Mrs. Taylor waved.", "She left."]),
    ("Titles and names", "French Madame", "Mme. Dupont arrive. Puis elle part.", ["Mme. Dupont arrive.", "Puis elle part."]),
    ("Titles and names", "French Mademoiselle", "Mlle. Martin travaille. Ensuite elle rentre.", ["Mlle. Martin travaille.", "Ensuite elle rentre."]),
    ("Titles and names", "Spanish señorita", "Srta. Álvarez llegó. Luego salió.", ["Srta. Álvarez llegó.", "Luego salió."]),
    ("Titles and names", "Portuguese professora", "Profa. Almeida falou. Depois saiu.", ["Profa. Almeida falou.", "Depois saiu."]),
    ("Titles and names", "Italian dottore", "Dott. Bianchi arrivò. Poi uscì.", ["Dott. Bianchi arrivò.", "Poi uscì."]),
    ("Titles and names", "Polish blessed", "bł. Karolina jest znana. Koniec.", ["bł. Karolina jest znana.", "Koniec."]),
    ("Titles and names", "Polish street proper name", "ul. Mickiewicza jest blisko. Potem wrócili.", ["ul. Mickiewicza jest blisko.", "Potem wrócili."]),
    ("Titles and names", "Polish avenue proper name", "al. Jana jest blisko. Potem wrócili.", ["al. Jana jest blisko.", "Potem wrócili."]),
    ("Titles and names", "Ukrainian avenue", "просп. Свободи відкрито. Далі.", ["просп. Свободи відкрито.", "Далі."]),
    ("Titles and names", "Ukrainian named after full", "школа ім. Лесі Українки працює. Далі.", ["школа ім. Лесі Українки працює.", "Далі."]),
    ("Titles and names", "Ukrainian named professor", "проф. Сидоренко відповів. Наступне питання.", ["проф. Сидоренко відповів.", "Наступне питання."]),
    ("Titles and names", "Russian alley", "пер. Лесной закрыт. Потом ушли.", ["пер. Лесной закрыт.", "Потом ушли."]),
    ("Titles and names", "Norwegian honorific", "Hr. Jensen svarte. Så gikk han.", ["Hr. Jensen svarte.", "Så gikk han."]),
    ("Titles and names", "Norwegian maiden title", "Frk. Hansen svarte. Så gikk hun.", ["Frk. Hansen svarte.", "Så gikk hun."]),
    ("Titles and names", "Turkish academic", "Doç. Kaya geldi. Sonra çıktı.", ["Doç. Kaya geldi.", "Sonra çıktı."]),
    ("Titles and names", "Hebrew professor without case", "פרופ. דוד דיבר. אחר כך הלך.", ["פרופ. דוד דיבר.", "אחר כך הלך."]),
    ("Titles and names", "Thai assistant professor", "ผศ. สมชายมาแล้ว. จบ.", ["ผศ. สมชายมาแล้ว.", "จบ."]),
    ("Titles and names", "Thai physician", "นพ. สมชายมาแล้ว. จบ.", ["นพ. สมชายมาแล้ว.", "จบ."]),
    ("Titles and names", "Vietnamese professor", "GS. Trần phát biểu. Xong.", ["GS. Trần phát biểu.", "Xong."]),
    ("Titles and names", "Vietnamese associate professor", "PGS. Nguyễn phát biểu. Xong.", ["PGS. Nguyễn phát biểu.", "Xong."]),
    ("Titles and names", "Multi-prefix title within one sentence", "Dr. Smith met Prof. Green at St. Peter Hospital. They spoke.", ["Dr. Smith met Prof. Green at St. Peter Hospital.", "They spoke."]),

    // Numbers and references
    ("Numbers and references", "English number fourteen", "No. 14 is the right room. Go there.", ["No. 14 is the right room.", "Go there."]),
    ("Numbers and references", "European reference nr", "Room nr. 7 is free. Come in.", ["Room nr. 7 is free.", "Come in."]),
    ("Numbers and references", "Figure twelve", "Fig. 12 shows the chart. Continue.", ["Fig. 12 shows the chart.", "Continue."]),
    ("Numbers and references", "Chapter four", "See ch. 4 for details. Then stop.", ["See ch. 4 for details.", "Then stop."]),
    ("Numbers and references", "Section seven", "Read sec. 7 carefully. Then sign.", ["Read sec. 7 carefully.", "Then sign."]),
    ("Numbers and references", "Article two", "Art. 2 is relevant. Continue.", ["Art. 2 is relevant.", "Continue."]),
    ("Numbers and references", "Numbered pages", "Read pp. 12–15 carefully. Then stop.", ["Read pp. 12–15 carefully.", "Then stop."]),
    ("Numbers and references", "Norwegian clock", "Vi møtes kl. 08:30 i morgen. Velkommen.", ["Vi møtes kl. 08:30 i morgen.", "Velkommen."]),
    ("Numbers and references", "Approximation with comma decimal", "It was approx. 3,14 seconds. Done.", ["It was approx. 3,14 seconds.", "Done."]),
    ("Numbers and references", "Approximate quantity", "It is ca. 150 metres away. Walk there.", ["It is ca. 150 metres away.", "Walk there."]),
    ("Numbers and references", "Multiple numeric abbreviations", "See Fig. 2 in Vol. 3 on p. 15. Read it.", ["See Fig. 2 in Vol. 3 on p. 15.", "Read it."]),
    ("Numbers and references", "Figure reference ends sentence", "See Fig. 2. Continue reading.", ["See Fig. 2.", "Continue reading."]),
    ("Numbers and references", "Page reference ends sentence", "Read p. 15. Turn the page.", ["Read p. 15.", "Turn the page."]),
    ("Numbers and references", "Volume reference ends sentence", "Check Vol. 3. Read the appendix.", ["Check Vol. 3.", "Read the appendix."]),
    ("Numbers and references", "Room number ends sentence", "Go to No. 12. Ask at reception.", ["Go to No. 12.", "Ask at reception."]),
    ("Numbers and references", "Norwegian reference ends sentence", "Se fig. 2. Les videre.", ["Se fig. 2.", "Les videre."]),
    ("Numbers and references", "Numeric abbreviation before sentence", "Refer to p. 7. The table follows.", ["Refer to p. 7.", "The table follows."]),
    ("Numbers and references", "Known abbreviation at sentence end", "The last figure is approx. Next section.", ["The last figure is approx.", "Next section."]),
    ("Numbers and references", "Number followed by real sentence", "The result is 3. Next step.", ["The result is 3.", "Next step."]),
    ("Numbers and references", "Measurement unit with sentence end", "It took 5 ms. The server responded.", ["It took 5 ms.", "The server responded."]),
    ("Numbers and references", "Time abbreviation at sentence end", "The event ended at 5 p.m. We left.", ["The event ended at 5 p.m.", "We left."]),

    // Examples and clauses
    ("Examples and clauses", "English e.g. uppercase", "Use e.g. Python today. Continue.", ["Use e.g. Python today.", "Continue."]),
    ("Examples and clauses", "English e.g. comma uppercase", "Use e.g., Python today. Continue.", ["Use e.g., Python today.", "Continue."]),
    ("Examples and clauses", "English i.e. comma uppercase", "The answer is i.e., John must decide. Next.", ["The answer is i.e., John must decide.", "Next."]),
    ("Examples and clauses", "Norwegian f.eks. comma uppercase", "Velg f.eks., Oslo som mål. Deretter Bergen.", ["Velg f.eks., Oslo som mål.", "Deretter Bergen."]),
    ("Examples and clauses", "Norwegian bl.a. with comma", "Vi besøkte bl.a., Bergen på turen. Hjem igjen.", ["Vi besøkte bl.a., Bergen på turen.", "Hjem igjen."]),
    ("Examples and clauses", "Swedish t.ex. before proper noun", "Välj t.ex. Göteborg. Vi åker snart.", ["Välj t.ex. Göteborg.", "Vi åker snart."]),
    ("Examples and clauses", "German z.B. with comma", "Nehmen wir z.B., Berlin als Beispiel. Danach weiter.", ["Nehmen wir z.B., Berlin als Beispiel.", "Danach weiter."]),
    ("Examples and clauses", "German d.h. before noun", "Es bedeutet d.h. Deutsch als Sprache. Danach weiter.", ["Es bedeutet d.h. Deutsch als Sprache.", "Danach weiter."]),
    ("Examples and clauses", "Latin cf before name", "Compare cf. Smith for context. Then read on.", ["Compare cf. Smith for context.", "Then read on."]),
    ("Examples and clauses", "English vs before proper name", "It is Green vs. Blue today. Later we decide.", ["It is Green vs. Blue today.", "Later we decide."]),
    ("Examples and clauses", "Abbreviation after clause semicolon", "We found errors, etc.; however, we continued. Then left.", ["We found errors, etc.; however, we continued.", "Then left."]),
    ("Examples and clauses", "Etc with lowercase continuation", "We bought apples, etc. before leaving. Next.", ["We bought apples, etc. before leaving.", "Next."]),
    ("Examples and clauses", "Etc before uppercase sentence", "We bought apples, etc. Next we left.", ["We bought apples, etc.", "Next we left."]),
    ("Examples and clauses", "Et al in publication year", "Taylor et al. (2024) agree. Next study.", ["Taylor et al. (2024) agree.", "Next study."]),
    ("Examples and clauses", "Et al lowercase in same sentence", "Taylor et al. reported it. Another study agrees.", ["Taylor et al. reported it.", "Another study agrees."]),
    ("Examples and clauses", "Et al before uppercase sentence", "Taylor et al. Next study.", ["Taylor et al.", "Next study."]),
    ("Examples and clauses", "Polish al avenue not et al", "al. Jana jest blisko. Dziękuję.", ["al. Jana jest blisko.", "Dziękuję."]),
    ("Examples and clauses", "Abbreviation inside quotes", "The note says \"e.g. Python\" is enough. Next.", ["The note says \"e.g. Python\" is enough.", "Next."]),

    // Technical tokens
    ("Technical tokens", "Version v1.0.8", "Version v1.0.8 is installed. Next.", ["Version v1.0.8 is installed.", "Next."]),
    ("Technical tokens", "Version v2.3.1", "Open v2.3.1 first. Then continue.", ["Open v2.3.1 first.", "Then continue."]),
    ("Technical tokens", "Decimal five digits", "The value is 3.14159 today. Done.", ["The value is 3.14159 today.", "Done."]),
    ("Technical tokens", "IPv4 local server", "Call 192.168.1.25:5045 now. Then disconnect.", ["Call 192.168.1.25:5045 now.", "Then disconnect."]),
    ("Technical tokens", "Localhost host and port", "Send to 127.0.0.1:8080 today. Done.", ["Send to 127.0.0.1:8080 today.", "Done."]),
    ("Technical tokens", "URL with query dots and fragment", "Visit https://example.com/search?q=test&lang=en#section2 now. Then leave.", ["Visit https://example.com/search?q=test&lang=en#section2 now.", "Then leave."]),
    ("Technical tokens", "URL short AI host with version query", "Open https://x.ai/api?v=2.1.0&mode=fast now. Then exit.", ["Open https://x.ai/api?v=2.1.0&mode=fast now.", "Then exit."]),
    ("Technical tokens", "URL ending before new sentence", "Read https://example.com/test?q=hello. Next page.", ["Read https://example.com/test?q=hello.", "Next page."]),
    ("Technical tokens", "Email with periods", "Mail test.user+tts@example.co.uk now. Then go.", ["Mail test.user+tts@example.co.uk now.", "Then go."]),
    ("Technical tokens", "Domain without scheme", "Open server.example.ru today. Then rest.", ["Open server.example.ru today.", "Then rest."]),
    ("Technical tokens", "Domain that ends a sentence", "He works for x.ai. Tomorrow he starts a job.", ["He works for x.ai.", "Tomorrow he starts a job."]),
    ("Technical tokens", "Domain continuous sentence", "He opened x.ai and continued reading. Next.", ["He opened x.ai and continued reading.", "Next."]),
    ("Technical tokens", "File extension in sentence", "The file config.prod.json is valid. Read it.", ["The file config.prod.json is valid.", "Read it."]),
    ("Technical tokens", "Executable with dotted name", "Run Tsubaki.TTS.Server.exe once. Then listen.", ["Run Tsubaki.TTS.Server.exe once.", "Then listen."]),
    ("Technical tokens", "Csharp dotted namespace", "Call System.Text.Json.JsonSerializer.Serialize(obj) now. Next.", ["Call System.Text.Json.JsonSerializer.Serialize(obj) now.", "Next."]),
    ("Technical tokens", "Dotted package name", "Package Newtonsoft.Json v13.0.3 works. It is ready.", ["Package Newtonsoft.Json v13.0.3 works.", "It is ready."]),
    ("Technical tokens", "Csharp null propagation", "Use foo?.Bar() in code. Then test.", ["Use foo?.Bar() in code.", "Then test."]),
    ("Technical tokens", "Windows path and dots", "File C:\\Users\\Test\\App.v2\\data.json exists. Then stop.", ["File C:\\Users\\Test\\App.v2\\data.json exists.", "Then stop."]),
    ("Technical tokens", "Unix path and dots", "Open /home/user/v1.2.3/config.json now. Then stop.", ["Open /home/user/v1.2.3/config.json now.", "Then stop."]),
    ("Technical tokens", "NET framework dotted version", ".NET 10.0 is installed. We can begin.", [".NET 10.0 is installed.", "We can begin."]),
    ("Technical tokens", "HTTP protocol version", "Server uses HTTP/1.1 today. Then rest.", ["Server uses HTTP/1.1 today.", "Then rest."]),
    ("Technical tokens", "Identifier with multiple parts", "Variable object.property.method() is valid. Next.", ["Variable object.property.method() is valid.", "Next."]),
    ("Technical tokens", "Mixed-script identifier", "The name Web-розробка-v2 is accepted. Next.", ["The name Web-розробка-v2 is accepted.", "Next."]),

    ("Technical tokens", "Attached inequality", "Check a!=b before continuing. Next.", ["Check a!=b before continuing.", "Next."]),
    ("Technical tokens", "Null access without spaces", "Use foo?.Bar() here. Next.", ["Use foo?.Bar() here.", "Next."]),
    ("Technical tokens", "Terminal question following URL", "Is https://example.com/test?q=hello still one URL? Yes.", ["Is https://example.com/test?q=hello still one URL?", "Yes."]),

    // Unicode periods
    ("Unicode periods", "Fullwidth title", "Dr． Smith answered. Then left.", ["Dr． Smith answered.", "Then left."]),
    ("Unicode periods", "Small full stop title", "Dr﹒ Smith answered. Then left.", ["Dr﹒ Smith answered.", "Then left."]),
    ("Unicode periods", "One dot leader title", "Dr․ Smith answered. Then left.", ["Dr․ Smith answered.", "Then left."]),
    ("Unicode periods", "Fullwidth initial", "A． Smith answered. Then left.", ["A． Smith answered.", "Then left."]),
    ("Unicode periods", "One dot leader initial", "Q․ Rivera answered. Then left.", ["Q․ Rivera answered.", "Then left."]),
    ("Unicode periods", "Fullwidth dotted acronym", "U．S． Army moved. Next.", ["U．S． Army moved.", "Next."]),
    ("Unicode periods", "Fullwidth decimal", "Value 3．14 is accepted. Next.", ["Value 3．14 is accepted.", "Next."]),
    ("Unicode periods", "Small decimal", "Value 3﹒14 is accepted. Next.", ["Value 3﹒14 is accepted.", "Next."]),
    ("Unicode periods", "One dot leader decimal", "Value 3․14 is accepted. Next.", ["Value 3․14 is accepted.", "Next."]),
    ("Unicode periods", "Fullwidth version number", "Version v2．3．1 is accepted. Next.", ["Version v2．3．1 is accepted.", "Next."]),
    ("Unicode periods", "Western text tightly attached to fullwidth period", "Hello．World continued here. Next.", ["Hello．World continued here.", "Next."]),

    // Quoting and ellipses
    ("Quoting and ellipses", "Question in quotation with reporting clause", "\"Really?!\" she asked. Then left.", ["\"Really?!\" she asked.", "Then left."]),
    ("Quoting and ellipses", "Question in quotation without reporting clause", "\"Really?!\" Then she left.", ["\"Really?!\"", "Then she left."]),
    ("Quoting and ellipses", "Exclamation in quotation", "\"Stop!\" Then he left.", ["\"Stop!\"", "Then he left."]),
    ("Quoting and ellipses", "Reporting clause after quotation", "\"What?!\" she shouted. Next.", ["\"What?!\" she shouted.", "Next."]),
    ("Quoting and ellipses", "Quoted ellipsis followed by reporting", "He said, \"Wait...\" and looked away. Next.", ["He said, \"Wait...\" and looked away.", "Next."]),
    ("Quoting and ellipses", "Ellipsis and interrobang followed by report", "\"Really...?!\" he replied.", ["\"Really...?!\"", "he replied."]),
    ("Quoting and ellipses", "ASCII ellipsis hesitation lowercase", "Well... maybe we should continue. Next.", ["Well... maybe we should continue.", "Next."]),
    ("Quoting and ellipses", "Unicode ellipsis hesitation lowercase", "Well… maybe we should continue. Next.", ["Well… maybe we should continue.", "Next."]),
    ("Quoting and ellipses", "Unicode ellipsis followed uppercase", "This ends here… Next sentence begins.", ["This ends here…", "Next sentence begins."]),
    ("Quoting and ellipses", "ASCII ellipsis followed uppercase", "This ends here... Next sentence begins.", ["This ends here...", "Next sentence begins."]),
    ("Quoting and ellipses", "Repeated six dots with uppercase", "This ends here...... Next sentence begins.", ["This ends here......", "Next sentence begins."]),
    ("Quoting and ellipses", "Repeated emphatic punctuation", "What?!?! Another question follows.", ["What?!?!", "Another question follows."]),
    ("Quoting and ellipses", "Single interrobang", "Really‽ Next question.", ["Really‽", "Next question."]),
    ("Quoting and ellipses", "Question exclamation mark", "Really⁈ Next question.", ["Really⁈", "Next question."]),
    ("Quoting and ellipses", "Exclamation question mark", "Really⁉ Next question.", ["Really⁉", "Next question."]),
    ("Quoting and ellipses", "Question with glued word", "Really?No separation yet. End.", ["Really?No separation yet.", "End."]),
    ("Quoting and ellipses", "Parentheses with terminator", "We asked (Ready?). Then went.", ["We asked (Ready?).", "Then went."]),
    ("Quoting and ellipses", "Nested punctuation before new sentence", "He shouted \"Stop!\" Then walked.", ["He shouted \"Stop!\"", "Then walked."]),
    ("Quoting and ellipses", "Japanese hesitation before uncased text", "えっと…どうしよう。次です。", ["えっと…どうしよう。", "次です。"]),
    ("Quoting and ellipses", "Chinese hesitation before uncased text", "等等……我还没说完。好了。", ["等等……我还没说完。", "好了。"]),
    ("Quoting and ellipses", "Ukrainian hesitation lowercase", "Я… не знаю, що сказати. Далі.", ["Я… не знаю, що сказати.", "Далі."]),

    // Script terminators
    ("Script terminators", "CJK Chinese no spaces", "这是第一句。下一句来了。", ["这是第一句。", "下一句来了。"]),
    ("Script terminators", "Japanese no spaces", "最初の文です。次の文です。", ["最初の文です。", "次の文です。"]),
    ("Script terminators", "Korean Japanese terminator", "처음입니다。다음입니다。", ["처음입니다。", "다음입니다。"]),
    ("Script terminators", "Fullwidth question no spaces", "本当？そうです！", ["本当？", "そうです！"]),
    ("Script terminators", "Arabic question and next sentence", "هل يعمل النظام؟ نعم، يعمل.", ["هل يعمل النظام؟", "نعم، يعمل."]),
    ("Script terminators", "Urdu full stop", "یہ درست ہے۔ اگلا جملہ۔", ["یہ درست ہے۔", "اگلا جملہ۔"]),
    ("Script terminators", "Greek semicolon question", "Είναι σωστό; Ναι, είναι.", ["Είναι σωστό;", "Ναι, είναι."]),
    ("Script terminators", "Armenian full stop", "Հայերեն փորձարկում։ Սա հայերեն նախադասություն է։", ["Հայերեն փորձարկում։", "Սա հայերեն նախադասություն է։"]),
    ("Script terminators", "Devanagari danda", "यह एक वाक्य है। यह दूसरा वाक्य है।", ["यह एक वाक्य है।", "यह दूसरा वाक्य है।"]),
    ("Script terminators", "Bengali danda", "এটি একটি বাক্য। এটি দ্বিতীয় বাক্য।", ["এটি একটি বাক্য।", "এটি দ্বিতীয় বাক্য।"]),
    ("Script terminators", "Thai traditional paragraph marker", "นี่คือประโยคภาษาไทย๚ นี่คือข้อความถัดไป๚", ["นี่คือประโยคภาษาไทย๚", "นี่คือข้อความถัดไป๚"]),
    ("Script terminators", "Myanmar section mark", "မြန်မာစာ စမ်းသပ်မှု။ ဒါက မြန်မာစာ ဖြစ်ပါတယ်။", ["မြန်မာစာ စမ်းသပ်မှု။", "ဒါက မြန်မာစာ ဖြစ်ပါတယ်။"]),
    ("Script terminators", "Hebrew sof pasuq", "זה כתוב׃ זה אחר׃", ["זה כתוב׃", "זה אחר׃"]),
    ("Script terminators", "Ethiopic full stop", "ሰላም። ይህ ሌላ ነው።", ["ሰላም።", "ይህ ሌላ ነው።"]),
    ("Script terminators", "Arabic question then Hebrew sentence", "هل هذا صحيح؟ זה נכון.", ["هل هذا صحيح؟", "זה נכון."]),
    ("Script terminators", "Greek followed by Cyrillic", "Είναι σωστό; Так, правильно.", ["Είναι σωστό;", "Так, правильно."]),
    ("Script terminators", "Mixed fullwidth marks", "Hello。World？Again！", ["Hello。", "World？", "Again！"]),

    // Line boundaries
    ("Line boundaries", "LF after abbreviation", "Dr.\nSmith arrived.", ["Dr.", "Smith arrived."]),
    ("Line boundaries", "CRLF after abbreviation", "Dr.\r\nSmith arrived.", ["Dr.", "Smith arrived."]),
    ("Line boundaries", "LF after dotted acronym", "The U.S.\nArmy arrived.", ["The U.S.", "Army arrived."]),
    ("Line boundaries", "LF plain text", "Hello world.\nNext line.", ["Hello world.", "Next line."]),
    ("Line boundaries", "Double LF paragraph", "First paragraph.\n\nSecond paragraph.", ["First paragraph.", "Second paragraph."]),
    ("Line boundaries", "Unicode line separator after title", "Prof.\u2028Smith arrived.", ["Prof.", "Smith arrived."]),
    ("Line boundaries", "Unicode paragraph separator", "First paragraph.\u2029Second paragraph.", ["First paragraph.", "Second paragraph."]),

    // Boundary counterexamples. U.S., Dr., A., and St. before capitalized text are
    // lexically ambiguous: their conservative expectations document heuristic limits,
    // not grammatically correct sentence boundaries for every reading of the input.
    ("Boundary counterexamples", "Ambiguous sentence-final U.S. keeps the initialism heuristic", "I live in the U.S. Today is sunny.", ["I live in the U.S. Today is sunny."]),
    ("Boundary counterexamples", "US introduces organization", "The U.S. Army arrived today. Then left.", ["The U.S. Army arrived today.", "Then left."]),
    ("Boundary counterexamples", "Dr title with person", "I spoke to Dr. Smith yesterday. Then left.", ["I spoke to Dr. Smith yesterday.", "Then left."]),
    ("Boundary counterexamples", "Ambiguous fragment-final Dr. keeps the title heuristic", "I spoke to Dr. Then I left.", ["I spoke to Dr. Then I left."]),
    ("Boundary counterexamples", "Initial before surname", "A. Smith arrived. Then spoke.", ["A. Smith arrived.", "Then spoke."]),
    ("Boundary counterexamples", "Ambiguous plan label A. keeps the uppercase-initial heuristic", "We chose Plan A. Tomorrow we try plan B.", ["We chose Plan A. Tomorrow we try plan B."]),
    ("Boundary counterexamples", "Etc ends sentence", "This is etc. Next sentence.", ["This is etc.", "Next sentence."]),
    ("Boundary counterexamples", "Etc introduces lowercase continuation", "We bought apples, etc. before leaving. Then went home.", ["We bought apples, etc. before leaving.", "Then went home."]),
    ("Boundary counterexamples", "Short domain ends sentence", "He works for x.ai. Tomorrow he starts a job.", ["He works for x.ai.", "Tomorrow he starts a job."]),
    ("Boundary counterexamples", "Short domain embedded in sentence", "He opened x.ai and continued reading. Then stopped.", ["He opened x.ai and continued reading.", "Then stopped."]),
    ("Boundary counterexamples", "Unit milliseconds not Ms", "It took 5 ms. Smith agreed.", ["It took 5 ms.", "Smith agreed."]),
    ("Boundary counterexamples", "Ms honorific before surname", "Ms. Smith agreed. Later she left.", ["Ms. Smith agreed.", "Later she left."]),
    ("Boundary counterexamples", "Title St before saint name", "We visited St. Olav today. We left.", ["We visited St. Olav today.", "We left."]),
    ("Boundary counterexamples", "Ambiguous St. keeps the name-binding heuristic", "We stopped on Main St. Peter left.", ["We stopped on Main St. Peter left."]),
    ("Boundary counterexamples", "Ordinary word with period", "This is the result. Next we continue.", ["This is the result.", "Next we continue."]),
    ("Boundary counterexamples", "Ordinary abbreviation before uppercase sentence", "It was approx. Next section starts.", ["It was approx.", "Next section starts."]),

    // Contextual boundaries found by the multilingual synthesis stress test.
    // Do not force EarlySplit: the manager may make only one early streaming cut.
    // An unknown lowercase single-letter ending can terminate a sentence. Uppercase
    // initials, uncased scripts, and explicitly registered abbreviations remain protected.
    ("Ambiguous letter endings", "Ukrainian final letter remains ambiguous — lowercase boundary", "Український текст має літери ї, є та ґ. Російський фрагмент починається.", ["Український текст має літери ї, є та ґ.", "Російський фрагмент починається."]),
    ("Ambiguous letter endings", "Russian final letter remains ambiguous — lowercase boundary", "Російський фрагмент має буквы ъ и э. Беларуская мова пачынаецца.", ["Російський фрагмент має буквы ъ и э.", "Беларуская мова пачынаецца."]),
    ("Ambiguous letter endings", "Belarusian final letter remains ambiguous — lowercase boundary", "Беларуская мова мае літару ў. Македонскиот тест почнува.", ["Беларуская мова мае літару ў.", "Македонскиот тест почнува."]),
    ("Ambiguous letter endings", "Macedonian final letter remains ambiguous — lowercase boundary", "Македонскиот тест содржи ѓ, ѕ и ќ. Српски текст следи.", ["Македонскиот тест содржи ѓ, ѕ и ќ.", "Српски текст следи."]),
    ("Ambiguous letter endings", "Serbian final letter remains ambiguous — lowercase boundary", "Српски текст садржи ђ, ћ, љ и њ. Наступне речення.", ["Српски текст садржи ђ, ћ, љ и њ.", "Наступне речення."]),
    ("Ambiguous letter endings", "Multilingual letter endings cannot be disambiguated lexically — lowercase boundary", "Український текст має літери ї, є та ґ. Російський фрагмент має буквы ъ и э. Беларуская мова мае літару ў. Македонскиот тест содржи ѓ, ѕ и ќ. Српски текст садржи ђ, ћ, љ и њ.", ["Український текст має літери ї, є та ґ.", "Російський фрагмент має буквы ъ и э.", "Беларуская мова мае літару ў.", "Македонскиот тест содржи ѓ, ѕ и ќ.", "Српски текст садржи ђ, ћ, љ и њ."]),
    ("Ambiguous letter endings", "One lowercase Cyrillic letter remains ambiguous — lowercase boundary", "Це літера ґ. Наступний приклад.", ["Це літера ґ.", "Наступний приклад."]),
    ("Ambiguous letter endings", "Latin letter list is ambiguous — lowercase boundary", "The last letters are x, y and z. Continue reading.", ["The last letters are x, y and z.", "Continue reading."]),
    ("Ambiguous letter endings", "Unregistered lowercase Latin initial uses the terminal heuristic", "a. Brown entered. Next.", ["a.", "Brown entered.", "Next."]),
    ("Ambiguous letter endings", "Unregistered lowercase Cyrillic initial uses the terminal heuristic", "а. Петренко відповів. Далі.", ["а.", "Петренко відповів.", "Далі."]),
    ("Ambiguous letter endings", "Uppercase Greek initial before surname stays together", "Ω. Παπαδόπουλος μίλησε. Τέλος.", ["Ω. Παπαδόπουλος μίλησε.", "Τέλος."]),
    ("Ambiguous letter endings", "Uppercase Latin option still ambiguous", "We chose Plan A. Tomorrow we try plan B.", ["We chose Plan A. Tomorrow we try plan B."]),
    ("Ambiguous letter endings", "Unregistered lowercase initial in prose uses the terminal heuristic", "We met a. Brown yesterday. Next meeting.", ["We met a.", "Brown yesterday.", "Next meeting."]),
    ("Ambiguous letter endings", "Conjunction does not protect an unregistered lowercase initial", "She spoke and a. Brown replied. Next item.", ["She spoke and a.", "Brown replied.", "Next item."]),
    ("Ambiguous letter endings", "Single-letter reference in prose without letter cue remains ambiguous — lowercase boundary", "The document mentions x. Smith agreed.", ["The document mentions x.", "Smith agreed."]),
    ("Ambiguous letter endings", "Capital letter name is ambiguous", "The final letter is Z. Continue reading.", ["The final letter is Z. Continue reading."]),
    ("Ambiguous letter endings", "Single letter after copula is ambiguous", "The letter is Z. Continue reading.", ["The letter is Z. Continue reading."]),
    ("Ambiguous letter endings", "Letter after past copula is ambiguous", "The final letter was Z. Continue reading.", ["The final letter was Z. Continue reading."]),
    ("Ambiguous letter endings", "Lowercase letter after copula is ambiguous — lowercase boundary", "The letter is z. Continue reading.", ["The letter is z.", "Continue reading."]),
    ("Ambiguous letter endings", "Capital letter list remains ambiguous", "The letters are X, Y and Z. Continue reading.", ["The letters are X, Y and Z. Continue reading."]),
    // Structural boundaries work across scripts without identifying any particular language.
    ("Ambiguous letter endings", "Explicit newline after letter resolves ambiguity", "The final letter is Z.\nContinue reading.", ["The final letter is Z.", "Continue reading."]),
    ("Ambiguous letter endings", "Explicit punctuation after isolated letter resolves ambiguity", "The final letter is Z! Continue reading.", ["The final letter is Z!", "Continue reading."]),
    ("Ambiguous letter endings", "Mixed script hard terminators remain universal", "Hello。Привіт？Γεια！Next.", ["Hello。", "Привіт？", "Γεια！", "Next."]),
    ("Ambiguous letter endings", "Initial before different-script name is still protected", "Ω. Smith spoke. Later we left.", ["Ω. Smith spoke.", "Later we left."]),
    ("Ambiguous letter endings", "Lowercase Greek letter ends sentence", "Το γράμμα είναι ω. Συνέχεια.", ["Το γράμμα είναι ω.", "Συνέχεια."]),
    ("Ambiguous letter endings", "Lowercase Latin with lowercase continuation", "The symbol is z. continue quietly.", ["The symbol is z.", "continue quietly."]),
    ("Ambiguous letter endings", "Uppercase Cyrillic initial stays attached", "Ґ. Коваленко відповів. Далі.", ["Ґ. Коваленко відповів.", "Далі."]),
    ("Ambiguous letter endings", "Built-in single-letter abbreviation remains protected", "г. Москва велика. Наступне речення.", ["г. Москва велика.", "Наступне речення."]),
    ("Ambiguous letter endings", "Lowercase compatibility full stop", "The last symbol is z． Continue.", ["The last symbol is z．", "Continue."]),
    ("Ambiguous letter endings", "Lowercase with different-script continuation", "The final letter is z. Український текст.", ["The final letter is z.", "Український текст."]),
    ("Ambiguous letter endings", "Unknown lowercase Cyrillic followed by a digit", "Літера ґ. 5 прикладів далі.", ["Літера ґ.", "5 прикладів далі."]),
    ("Ambiguous letter endings", "Georgian Mkhedruli initial remains attached", "გ. გიორგი მოვიდა. შემდეგ წავიდა.", ["გ. გიორგი მოვიდა.", "შემდეგ წავიდა."]),
    ("Ambiguous letter endings", "Other hard terminal after lowercase letter", "The letter is z! Continue.", ["The letter is z!", "Continue."]),
    ("Greek contextual questions", "ASCII semicolon between Greek sentences", "Είναι αυτό ένα ελληνικό κείμενο; Ναι… ίσως!", ["Είναι αυτό ένα ελληνικό κείμενο;", "Ναι… ίσως!"]),
    ("Greek contextual questions", "Explicit U+037E Greek question mark", "Είναι σωστό; Ναι, είναι.", ["Είναι σωστό;", "Ναι, είναι."]),
    ("Greek contextual questions", "Greek ASCII semicolon followed by lowercase answer", "Είναι σωστό; ναι, είναι.", ["Είναι σωστό;", "ναι, είναι."]),
    ("Greek contextual questions", "Greek ASCII semicolon followed by English answer", "Είναι σωστό; Yes, it is.", ["Είναι σωστό;", "Yes, it is."]),
    ("Greek contextual questions", "Greek question after Latin introduction", "She asked: Είναι σωστό; Ναι.", ["She asked: Είναι σωστό;", "Ναι."]),
    ("Greek contextual questions", "Quoted Greek question with reporting clause", "«Τι συμβαίνει;» — ρώτησε κάποιος. Μετά έφυγε.", ["«Τι συμβαίνει;» — ρώτησε κάποιος.", "Μετά έφυγε."]),
    ("Greek contextual questions", "Quoted U+037E with reporting clause", "«Τι συμβαίνει;» — ρώτησε κάποιος. Μετά έφυγε.", ["«Τι συμβαίνει;» — ρώτησε κάποιος.", "Μετά έφυγε."]),
    ("Greek contextual questions", "Greek quoted question without reporting clause", "«Τι συμβαίνει;» Έφυγε.", ["«Τι συμβαίνει;»", "Έφυγε."]),
    ("Greek contextual questions", "Greek ano teleia is a clause separator", "Είναι σωστό· συνεχίζουμε. Άλλη πρόταση.", ["Είναι σωστό· συνεχίζουμε.", "Άλλη πρόταση."]),
    ("Greek contextual questions", "English semicolon remains intra-sentence", "First; second, third. Fourth.", ["First; second, third.", "Fourth."]),
    ("Greek contextual questions", "Greek words with Latin semicolon before Greek quote", "Hello; Είναι σωστό; Ναι.", ["Hello; Είναι σωστό;", "Ναι."]),
    ("Greek contextual questions", "Greek question with spacing before ASCII semicolon", "Είναι σωστό ; Ναι.", ["Είναι σωστό ;", "Ναι."]),
    ("Greek contextual questions", "Latin semicolon before Greek prose is a clause", "Hello; Ελληνικά κείμενα. Next.", ["Hello; Ελληνικά κείμενα.", "Next."]),
    ("Greek contextual questions", "Greek quotation with reporting dash", "«Είναι σωστό;» — είπε εκείνη. Μετά έφυγε.", ["«Είναι σωστό;» — είπε εκείνη.", "Μετά έφυγε."]),
    ("Greek contextual questions", "English quoted reporting dash stays in sentence", "\"Really?\" — she asked. Then left.", ["\"Really?\" — she asked.", "Then left."]),

    // Long-form mixed
    ("Long-form mixed", "English technical paragraph", "Dr. Morgan opened version v2.3.1 at 10.5 ms, and Prof. Smith said it worked. The project S.T.A.L.K.E.R. is still running, while U.S.A. remains part of this sentence. Visit https://example.com/search?q=test&lang=en#section2 before continuing.", ["Dr. Morgan opened version v2.3.1 at 10.5 ms, and Prof. Smith said it worked.", "The project S.T.A.L.K.E.R. is still running, while U.S.A. remains part of this sentence.", "Visit https://example.com/search?q=test&lang=en#section2 before continuing."]),
    ("Long-form mixed", "Dialogue reporting clauses", "\"Really?!\" she asked. \"Yes!\" he answered. (Are you sure?) Absolutely. Wait... maybe I was wrong.", ["\"Really?!\" she asked.", "\"Yes!\" he answered.", "(Are you sure?)", "Absolutely.", "Wait... maybe I was wrong."]),
    ("Long-form mixed", "English mixed technical items", "The value is 3.14159, not 3. The release is v1.0.8, and .NET 10.0 is installed. File config.prod.json belongs to the same sentence. The executable Tsubaki.TTS.Server.exe is here.", ["The value is 3.14159, not 3.", "The release is v1.0.8, and .NET 10.0 is installed.", "File config.prod.json belongs to the same sentence.", "The executable Tsubaki.TTS.Server.exe is here."]),
    ("Long-form mixed", "Ukrainian technical paragraph", "Тепер український текст. Пан Іван сказав, що версія v2.4.1 працює нормально. Д-р Петренко перевірив файл config.test.json і адресу https://example.org/test?q=українська.", ["Тепер український текст.", "Пан Іван сказав, що версія v2.4.1 працює нормально.", "Д-р Петренко перевірив файл config.test.json і адресу https://example.org/test?q=українська."]),
    ("Long-form mixed", "Multilingual single sentences", "English starts here, але посередині ми раптом переходимо українською мовою, and then return to English without ending the sentence. Це нове українське речення, but it contains a short English phrase inside. Наступне речення містить English, русский текст и снова українську мову в одному semantic sentence.", ["English starts here, але посередині ми раптом переходимо українською мовою, and then return to English without ending the sentence.", "Це нове українське речення, but it contains a short English phrase inside.", "Наступне речення містить English, русский текст и снова українську мову в одному semantic sentence."]),
    ("Long-form mixed", "Mixed scripts terminal punctuation", "Hello。World？Again！Next sentence. هل يعمل النظام؟ نعم، يعمل. Սա հայերեն նախադասություն է։", ["Hello。", "World？", "Again！", "Next sentence.", "هل يعمل النظام؟", "نعم، يعمل.", "Սա հայերեն նախադասություն է։"]),
    ("Long-form mixed", "Names examples and references", "Dr. Smith met Prof. Green on St. Peter St. today. They discussed Fig. 2 from Vol. 3 at 10.5 ms per step. We may use e.g., Python in the next release.", ["Dr. Smith met Prof. Green on St. Peter St. today.", "They discussed Fig. 2 from Vol. 3 at 10.5 ms per step.", "We may use e.g., Python in the next release."]),
    ("Long-form mixed", "Mixed-language punctuation and URLs", "Maya checked config.prod.json—twice—and whispered, \"No... esto no funciona; but maybe, demain, ça ira?\", then added (almost laughing): Я перевірю ще раз — pero, please, don't restart https://example.com/api?v=2.3.1&mode=fast; if Dr. Smith replies, say \"sí, d'accord\", otherwise... wait.", ["Maya checked config.prod.json—twice—and whispered, \"No... esto no funciona; but maybe, demain, ça ira?\", then added (almost laughing): Я перевірю ще раз — pero, please, don't restart https://example.com/api?v=2.3.1&mode=fast; if Dr. Smith replies, say \"sí, d'accord\", otherwise... wait."]),
};


// These deliberately synthetic forms validate syntax heuristics, not lexicon coverage.
// Avoid adding them to production abbreviation lists just to make a test pass.
var ruleCases = new List<(string Category, string Name, string Input, string[] Expected)>();

// Unknown initials, including non-Latin and scripts without letter case.
var initialSamples = new (string Script, string Initial, string Surname, string Verb, string Next)[]
{
    ("Latin extended Z", "Ƶ", "Novak", "spoke", "Next"),
    ("Latin extended L", "Ł", "Kowalski", "spoke", "Next"),
    ("Latin extended eth", "Đ", "Nguyễn", "spoke", "Next"),
    ("Latin Icelandic", "Þ", "Jónsson", "spoke", "Next"),
    ("Latin Turkish", "Ğ", "Yılmaz", "spoke", "Next"),
    ("Latin Baltic", "Ū", "Kalniņš", "spoke", "Next"),
    ("Cyrillic Ukrainian", "Ґ", "Коваленко", "відповів", "Далі"),
    ("Cyrillic Belarusian", "Ў", "Савіч", "адказаў", "Далей"),
    ("Cyrillic Serbian", "Љ", "Јовановић", "говорио", "Даље"),
    ("Cyrillic Macedonian", "Ѓ", "Стојанов", "зборуваше", "Потоа"),
    ("Greek", "Ψ", "Παπαδόπουλος", "μίλησε", "Τέλος"),
    ("Armenian", "Ա", "Սարգսյան", "խոսեց", "Վերջ"),
    ("Georgian", "გ", "გიორგი", "მოვიდა", "შემდეგ"),
    ("Hebrew", "א", "כהן", "דיבר", "המשך"),
    ("Arabic", "ع", "أحمد", "وصل", "ثم"),
};

foreach (var sample in initialSamples)
{
    string first = $"{sample.Initial}. {sample.Surname} {sample.Verb}.";
    string second = $"{sample.Next}.";
    ruleCases.Add(("Unlisted initials", $"{sample.Script} initial", $"{first} {second}", [first, second]));
}

// Georgian Mkhedruli is Unicode lowercase but has no ordinary sentence-initial casing.
// Unknown single-letter initials and recognized title abbreviations must still stay attached.
ruleCases.Add(("Georgian prose", "Ordinary Georgian sentences",
    "თბილისი ლამაზია. ხვალ მოვდივარ.",
    ["თბილისი ლამაზია.", "ხვალ მოვდივარ."]));
ruleCases.Add(("Georgian prose", "Georgian saint abbreviation",
    "წმ. გიორგი მოვიდა. შემდეგ წავიდა.",
    ["წმ. გიორგი მოვიდა.", "შემდეგ წავიდა."]));
ruleCases.Add(("Georgian prose", "Three Georgian sentences",
    "დღეს კარგი დღეა. ხვალ სხვა დღეა. მერე დავისვენებთ.",
    ["დღეს კარგი დღეა.", "ხვალ სხვა დღეა.", "მერე დავისვენებთ."]));
ruleCases.Add(("Georgian prose", "Latin sentence then Georgian sentence",
    "The process completed. შემდეგ დაიწყო.",
    ["The process completed.", "შემდეგ დაიწყო."]));
ruleCases.Add(("Georgian prose", "Latin title before Georgian name",
    "Dr. გიორგი მოვიდა. შემდეგ დაბრუნდა.",
    ["Dr. გიორგი მოვიდა.", "შემდეგ დაბრუნდა."]));

// Each code point below is an explicit test input, not sourced from TextChunker.SentenceTerminators.
// This makes a regression visible if the production set accidentally loses a character.
var hardTerminators = new (string Name, char Mark)[]
{
    ("NKo exclamation", '\u07F9'),
    ("Syriac paragraph", '\u0700'),
    ("Syriac upper full stop", '\u0701'),
    ("Syriac lower full stop", '\u0702'),
    ("Indic double danda", '\u0965'),
    ("Sinhala kunddaliya", '\u0DF4'),
    ("Thai khomut", '\u0E5B'),
    ("Khmer khan", '\u17D4'),
    ("Khmer bariyoosan", '\u17D5'),
    ("Khmer koomuut", '\u17DA'),
    ("Limbu exclamation", '\u1944'),
    ("Limbu question", '\u1945'),
    ("Balinese carik pareren", '\u1B5F'),
    ("Ol Chiki mucaad", '\u1C7E'),
    ("Ol Chiki double mucaad", '\u1C7F'),
    ("Mongolian full stop", '\u1803'),
    ("Manchu full stop", '\u1809'),
    ("Canadian syllabics full stop", '\u166E'),
    ("Vai full stop", '\uA60E'),
    ("Vai question", '\uA60F'),
    ("Bamum full stop", '\uA6F3'),
    ("Bamum question", '\uA6F7'),
    ("Saurashtra danda", '\uA8CE'),
    ("Saurashtra double danda", '\uA8CF'),
    ("Rejang section", '\uA95F'),
    ("Javanese pada lungsi", '\uA9C9'),
    ("Cham danda", '\uAA5D'),
    ("Cham double danda", '\uAA5E'),
    ("Cham triple danda", '\uAA5F'),
    ("Meetei Mayek cheikhei", '\uABEB'),
    ("Ethiopic question", '\u1367'),
    ("Ethiopic paragraph", '\u1368'),
    ("Halfwidth ideographic stop", '\uFF61'),
    ("Vertical ideographic stop", '\uFE12'),
    ("Vertical exclamation", '\uFE15'),
    ("Vertical question", '\uFE16'),
    ("Small exclamation", '\uFE57'),
    ("Small question", '\uFE56'),
    ("Compound double exclamation", '\u203C'),
    ("Compound double question", '\u2047'),
};

foreach (var (name, mark) in hardTerminators)
{
    string first = $"Alpha{mark}";
    string second = $"Beta{mark}";
    string separator = mark is '\u203C' or '\u2047' ? " " : "";
    ruleCases.Add(("Hard terminator matrix", name, first + separator + second, [first, second]));
}

// Unknown acronym-like forms: no title/abbreviation dictionary entries are required.
var dottedSamples = new (string Name, string Acronym, string Noun, string Verb, string Next)[]
{
    ("Latin alphabetic three", "Q.Z.F.", "Group", "met", "Later"),
    ("Latin alphabetic four", "V.X.Y.Z.", "Lab", "opened", "Later"),
    ("Latin mixed case", "q.X.Z.", "Group", "met", "Later"),
    ("Cyrillic Ukrainian", "Ж.Ї.Ґ.", "Комітет", "засідав", "Потім"),
    ("Cyrillic Serbian", "Љ.Њ.Ћ.", "Одбор", "радио", "После"),
    ("Greek", "Ψ.Ω.Δ.", "Ομάδα", "μίλησε", "Τέλος"),
    ("Armenian", "Ա.Բ.Գ.", "Խումբը", "եկավ", "Հետո"),
    ("Vietnamese Latin letters", "Đ.Ơ.Ư.", "Team", "worked", "Next"),
};

foreach (var sample in dottedSamples)
{
    string first = $"{sample.Acronym} {sample.Noun} {sample.Verb}.";
    string second = $"{sample.Next}.";
    ruleCases.Add(("Unlisted dotted chains", sample.Name, $"{first} {second}", [first, second]));
}

// Re-check the same generic rule with every supported period-like compatibility glyph.
var periodForms = new (string Name, char Period)[]
{
    ("one dot leader", '\u2024'),
    ("small full stop", '\uFE52'),
    ("fullwidth full stop", '\uFF0E'),
};

foreach (var (name, period) in periodForms)
{
    string initial = $"Ƶ{period} Novak arrived.";
    string dotted = $"Q{period}Z{period}F{period} Group arrived.";
    string number = $"Value 8{period}625 is valid.";
    ruleCases.Add(("Period form matrix", $"{name} unknown initial", $"{initial} Next.", [initial, "Next."]));
    ruleCases.Add(("Period form matrix", $"{name} unknown acronym", $"{dotted} Next.", [dotted, "Next."]));
    ruleCases.Add(("Period form matrix", $"{name} decimal", $"{number} Next.", [number, "Next."]));
}

// Hand-selected generic cases check positive and negative outcomes, not only list membership.
var explicitRuleCases = new (string Category, string Name, string Input, string[] Expected)[]
{
    ("Generic syntax", "Supplementary Deseret initial", "𐐀. Smith entered. Next.", ["𐐀. Smith entered.", "Next."]),
    ("Generic syntax", "Supplementary Osage initial", "𐓀. Doe entered. Next.", ["𐓀. Doe entered.", "Next."]),
    ("Generic syntax", "Unlisted lowercase chain before lowercase", "We wrote q.x.z. as a marker. Next.", ["We wrote q.x.z. as a marker.", "Next."]),
    ("Generic syntax", "Unlisted lowercase chain ends before uppercase", "We wrote q.x.z. Next operation.", ["We wrote q.x.z.", "Next operation."]),
    ("Generic syntax", "Uppercase and lowercase chain", "The q.X.Z. Group met. Next.", ["The q.X.Z. Group met.", "Next."]),
    ("Generic syntax", "Unknown plain word before uppercase", "The machine stopped. Another process started.", ["The machine stopped.", "Another process started."]),
    ("Generic syntax", "Unknown plain word before lowercase", "We marked zqv. as the code. Next.", ["We marked zqv. as the code.", "Next."]),
    ("Generic syntax", "Unknown uppercase word ends sentence", "This module is READY. Another follows.", ["This module is READY.", "Another follows."]),
    ("Generic syntax", "Initial joined to hyphen surname", "Ƶ. Smith-Jones entered. Next.", ["Ƶ. Smith-Jones entered.", "Next."]),
    ("Generic syntax", "Initial with apostrophe surname", "Ƶ. O'Connor entered. Next.", ["Ƶ. O'Connor entered.", "Next."]),
    ("Generic syntax", "Initial followed Arabic numeral", "Item Ƶ. 7 was tested. Next.", ["Item Ƶ. 7 was tested.", "Next."]),
    ("Generic syntax", "Initial followed Arabic-Indic numeral", "Item Ƶ. ٧ was tested. Next.", ["Item Ƶ. ٧ was tested.", "Next."]),
    ("Generic syntax", "Unknown dotted chain followed number", "Tag Q.Z.F. 12 was noted. Next.", ["Tag Q.Z.F. 12 was noted.", "Next."]),
    ("Generic syntax", "Unknown period within Arabic letters", "هذا رمز س.ص يستخدم هنا. ثم توقف.", ["هذا رمز س.ص يستخدم هنا.", "ثم توقف."]),
    ("Generic syntax", "Unknown period within Hebrew letters", "הסימן א.ב נכתב כאן. אחר כך.", ["הסימן א.ב נכתב כאן.", "אחר כך."]),
    ("Generic syntax", "Unknown period within Devanagari letters", "यह नाम क.ख इसी वाक्य में है। फिर नया वाक्य।", ["यह नाम क.ख इसी वाक्य में है।", "फिर नया वाक्य।"]),
    ("Generic syntax", "Unknown period within Chinese characters", "这是甲.乙标记。下一句。", ["这是甲.乙标记。", "下一句。"]),
    ("Generic syntax", "Unicode Arabic-Indic decimal", "The reading was ٣.١٤ units. Next.", ["The reading was ٣.١٤ units.", "Next."]),
    ("Generic syntax", "Unicode Devanagari decimal", "The reading was १२.३४ units. Next.", ["The reading was १२.३४ units.", "Next."]),
    ("Generic syntax", "Nested version like digits", "Build 7.18.204.6 finished. Next.", ["Build 7.18.204.6 finished.", "Next."]),
    ("Generic syntax", "Invisible RTL control after unknown initial", "א.\u200F כהן הגיע. המשך.", ["א.\u200F כהן הגיע.", "המשך."]),
    ("Generic syntax", "Nonbreaking space after unknown initial", "Ƶ.\u00A0Novak spoke. Next.", ["Ƶ.\u00A0Novak spoke.", "Next."]),
    ("Generic syntax", "Thin space after unknown initial", "Ƶ.\u2009Novak spoke. Next.", ["Ƶ.\u2009Novak spoke.", "Next."]),
    ("Generic syntax", "Ideographic space after unknown initial", "Ƶ.\u3000Novak spoke. Next.", ["Ƶ.\u3000Novak spoke.", "Next."]),
    ("Generic syntax", "Tab after unknown initial", "Ƶ.\tNovak spoke. Next.", ["Ƶ.\tNovak spoke.", "Next."]),
    ("Generic syntax", "Unicode ellipsis two dots lower continuation", "Wait‥ maybe continue. Next.", ["Wait‥ maybe continue.", "Next."]),
    ("Generic syntax", "Unicode ellipsis midline lower continuation", "Wait⋯ maybe continue. Next.", ["Wait⋯ maybe continue.", "Next."]),
    ("Generic syntax", "Mixed periods ellipsis lower continuation", "Wait.﹒． maybe continue. Next.", ["Wait.﹒． maybe continue.", "Next."]),
    ("Generic syntax", "Isolated unknown single letter lowercase — lowercase boundary", "z. Smith entered. Next.", ["z.", "Smith entered.", "Next."]),
};

ruleCases.AddRange(explicitRuleCases);
ruleCases.Add(("Generic syntax", "Uppercase initial with decomposed accent",
    "A\u0301. Novak spoke. Next.", ["A\u0301. Novak spoke.", "Next."]));
ruleCases.Add(("Generic syntax", "Cyrillic initial with decomposed diaeresis",
    "І\u0308. Коваленко відповів. Далі.", ["І\u0308. Коваленко відповів.", "Далі."]));
ruleCases.Add(("Generic syntax", "Lowercase letter with decomposed accent ends a sentence",
    "The last letter is a\u0301. Continue.", ["The last letter is a\u0301.", "Continue."]));
ruleCases.Add(("Generic syntax", "Titlecase dotted initials remain protected",
    "The ǅ.ǈ. Group met. Next.", ["The ǅ.ǈ. Group met.", "Next."]));
ruleCases.Add(("Generic syntax", "Supplementary lowercase letter ends a sentence",
    "The last letter is 𐐨. Continue.", ["The last letter is 𐐨.", "Continue."]));

var nameBindingCases = new List<(string Category, string Name, string Input, string[] Expected)>
{
    ("Name binding boundaries", "Unknown lowercase letter before uppercase", "The letter is x. Next sentence.", ["The letter is x.", "Next sentence."]),
    ("Name binding boundaries", "Unknown lowercase letter before lowercase", "The letter is x. next sentence.", ["The letter is x.", "next sentence."]),
    ("Name binding boundaries", "Unknown uppercase initial stays conservative", "The initial is X. Next sentence.", ["The initial is X. Next sentence."]),
    ("Name binding boundaries", "Unknown two-letter token before uppercase", "The token is zx. Next sentence.", ["The token is zx.", "Next sentence."]),
    ("Name binding boundaries", "Unknown two-letter token before lowercase", "The token is zx. next sentence.", ["The token is zx. next sentence."]),
    ("Name binding boundaries", "Uppercase two-letter token is not a single initial", "The token is ZX. Next sentence.", ["The token is ZX.", "Next sentence."]),
    ("Name binding boundaries", "Registered single-letter abbreviation binds its continuation", "г. Москва is large. Next sentence.", ["г. Москва is large.", "Next sentence."]),
    ("Name binding boundaries", "General abbreviation may finish a sentence", "They listed tools, libraries, etc. Next sentence.", ["They listed tools, libraries, etc.", "Next sentence."]),
    ("Name binding boundaries", "Chained lowercase prefixes keep the following name", "We met dr. id. Kovács today. Next.", ["We met dr. id. Kovács today.", "Next."]),
    ("Name binding boundaries", "Parenthesized lowercase prefix keeps the following name", "(id. Kovács arrived.) Next.", ["(id. Kovács arrived.)", "Next."])
};

// Each spelling has an explicit name-binding expectation. General abbreviations and
// unknown short tokens above remain counterexamples to indiscriminate prefix protection.
var hungarianPrefixes = new (string[] Spellings, string Surname)[]
{
    (["id", "Id", "ID"], "Kovács"),
    (["ifj", "Ifj", "IFJ"], "Szathmári"),
    (["özv", "Özv", "ÖZV"], "Kiss")
};

foreach (var prefix in hungarianPrefixes)
{
    foreach (string spelling in prefix.Spellings)
    foreach (char period in new[] { '.', '\u2024', '\uFE52', '\uFF0E' })
    {
        string first = $"We met {spelling}{period} {prefix.Surname} today.";
        nameBindingCases.Add(("Name binding boundaries", $"{spelling}; period=U+{(int)period:X4}",
            $"{first} Next.", [first, "Next."]));
    }

    string lowercase = prefix.Spellings[0];
    string firstLowercase = $"We met {lowercase}. {prefix.Surname.ToLowerInvariant()} today.";
    nameBindingCases.Add(("Name binding boundaries", $"{lowercase} before a lowercase continuation",
        $"{firstLowercase} Next.", [firstLowercase, "Next."]));
    nameBindingCases.Add(("Name binding boundaries", $"{lowercase} at end of input",
        $"The label is {lowercase}.", [$"The label is {lowercase}."]));
    nameBindingCases.Add(("Name binding boundaries", $"Explicit newline overrides {lowercase}",
        $"{lowercase}.\n{prefix.Surname} arrived. Next.", [$"{lowercase}.", $"{prefix.Surname} arrived.", "Next."]));
}

cases = [..cases, ..ruleCases, ..nameBindingCases];

// Verify sentence boundaries and completion flags with EarlySplit disabled.
var behaviorCases = new (string Category, string Name, string Input, string[] Expected,
    bool[] Finished)[]
{
    ("Completion flags", "No terminal punctuation", "Plain text without a period", ["Plain text without a period"], [false]),
    ("Completion flags", "No terminal Chinese", "这是中文没有终止符", ["这是中文没有终止符"], [false]),
    ("Completion flags", "No terminal after initial", "Ƶ. Novak arrived", ["Ƶ. Novak arrived"], [false]),
    ("Completion flags", "No terminal after unknown acronym", "Q.Z.F. Group arrived", ["Q.Z.F. Group arrived"], [false]),
    ("Completion flags", "Finished followed unfinished", "A complete sentence. Then an unfinished fragment", ["A complete sentence.", "Then an unfinished fragment"], [true, false]),
    ("Completion flags", "Unfinished Unicode script", "まだ続きます", ["まだ続きます"], [false]),
    ("Completion flags", "Trailing ellipsis is terminal", "Still thinking…", ["Still thinking…"], [true]),
    ("Completion flags", "Final question is terminal", "What now?", ["What now?"], [true]),
    ("Completion flags", "Leading whitespace without terminal", "  pending content  ", ["pending content"], [false]),
    ("Standard punctuation", "Comma stays within sentence", "First, second. Third.", ["First, second.", "Third."], [true, true]),
    ("Standard punctuation", "Semicolon stays within sentence", "First; second, third. Fourth.", ["First; second, third.", "Fourth."], [true, true]),
    ("Standard punctuation", "Colon stays within sentence", "Answer: the first one. Continue.", ["Answer: the first one.", "Continue."], [true, true]),
    ("Standard punctuation", "Colon inside technical token", "Use alpha:beta as a token. Continue.", ["Use alpha:beta as a token.", "Continue."], [true, true]),
    ("Standard punctuation", "URL scheme stays within sentence", "Open https://example.org/path now. Continue.", ["Open https://example.org/path now.", "Continue."], [true, true]),
    ("Standard punctuation", "Comma without whitespace", "Keep alpha,beta together. Continue.", ["Keep alpha,beta together.", "Continue."], [true, true]),
    ("Standard punctuation", "Arabic comma stays within sentence", "مرحبا، ثم ذهب. تابع.", ["مرحبا، ثم ذهب.", "تابع."], [true, true]),
    ("Standard punctuation", "Second sentence punctuation", "First sentence. Second, third.", ["First sentence.", "Second, third."], [true, true]),
    ("Standard punctuation", "Leading newline with comma", "\nFirst, second.", ["First, second."], [true]),
    ("Standard punctuation", "Abbreviation followed by comma", "Use e.g., Python today. Next.", ["Use e.g., Python today.", "Next."], [true, true]),
    ("Standard punctuation", "Unknown dotted abbreviation followed by comma", "The Q.R., a proposed standard, is ready. Next.", ["The Q.R., a proposed standard, is ready.", "Next."], [true, true]),

    // Common AI-generated numbered/lettered lists.
    ("AI ordered lists", "Two numbered steps", "1. Download the model. 2. Start the server.", ["1.", "Download the model.", "2.", "Start the server."], [true, true, true, true]),
    ("AI ordered lists", "List after completed sentence", "Done. 2. Start the server.", ["Done.", "2.", "Start the server."], [true, true, true]),
    ("AI ordered lists", "List after question", "Ready? 3. Open the settings.", ["Ready?", "3.", "Open the settings."], [true, true, true]),
    ("AI ordered lists", "Two-digit numbered steps", "10. Check the logs. 11. Restart the service.", ["10.", "Check the logs.", "11.", "Restart the service."], [true, true, true, true]),
    ("AI ordered lists", "Numbered steps across lines", "1. Launch the server.\n2. Send a request.", ["1.", "Launch the server.", "2.", "Send a request."], [true, true, true, true]),
    ("AI ordered lists", "Numbered item no final punctuation", "1. Prepare a fresh request", ["1.", "Prepare a fresh request"], [true, false]),
    ("AI ordered lists", "Parenthesized ordered steps", "2) Download the model. 3) Run the service.", ["2) Download the model.", "3) Run the service."], [true, true]),
    ("AI ordered lists", "Lettered ordered steps", "A. Check the logs. B. Restart the service.", ["A. Check the logs.", "B. Restart the service."], [true, true]),
    ("AI ordered lists", "Ordinary numerical end remains a boundary", "The answer is 1. Next step starts.", ["The answer is 1.", "Next step starts."], [true, true]),
    ("AI ordered lists", "Numbered list with IPA input", "1. Say [[həˈloʊ]] aloud. 2. Continue speaking.", ["1.", "Say [[həˈloʊ]] aloud.", "2.", "Continue speaking."], [true, true, true, true]),
    ("AI ordered lists", "List introduced by colon", "Steps: 1. Download the model. 2. Start the server.", ["Steps: 1.", "Download the model.", "2.", "Start the server."], [true, true, true, true]),
    ("AI ordered lists", "Numbered item lowercase text", "1. download the model. 2. start the server.", ["1.", "download the model.", "2.", "start the server."], [true, true, true, true]),
    // Natural ordered lists deliberately isolate the number to trigger a modest audio pause.
    ("AI ordered lists", "Three spoken steps", "1. Open the report.\n2. Check the results.\n3. Send the final response.", ["1.", "Open the report.", "2.", "Check the results.", "3.", "Send the final response."], [true, true, true, true, true, true]),
    ("AI ordered lists", "List marker without completed sentence", "1. Open the report", ["1.", "Open the report"], [true, false]),
    ("AI ordered lists", "Three-digit item number", "120. Read the report. 121. Continue reading.", ["120.", "Read the report.", "121.", "Continue reading."], [true, true, true, true]),
    ("AI ordered lists", "Number reference does not become list marker", "Read p. 15. Go to No. 12. Check Fig. 3.", ["Read p. 15.", "Go to No. 12.", "Check Fig. 3."], [true, true, true]),

    // Realistic speech overrides: only the selected word uses a raw pronunciation.
};

// Emergency MaxChunkLength must never cut through an atomic technical token.
// Unlike the ordinary corpus, these use a deliberately small chunk limit.
const string protectedUrl = "https://example.com/api?v=2.3.1&mode=fast";
const string longProtectedUrl = "https://example.com/really-long-path/with-more-parts?q=hello.world&mode=fast#part-2";
var emergencyCases = new (string Name, string Input, string Protected, bool EarlySplit)[]
{
    ("Long mixed prose URL", "Maya checked config.prod.json—twice—and whispered, \"No... esto no funciona; but maybe, demain, ça ira?\", then added (almost laughing): Я перевірю ще раз — pero, please, don't restart " + protectedUrl + "; if Dr. Smith replies, wait.", protectedUrl, false),
    ("Long mixed prose URL with EarlySplit", "Maya checked config.prod.json—twice—and whispered, \"No... esto no funciona; but maybe, demain, ça ira?\", then added (almost laughing): Я перевірю ще раз — pero, please, don't restart " + protectedUrl + "; if Dr. Smith replies, wait.", protectedUrl, true),
    ("URL starts near emergency size limit", "This is a deliberately long introduction before the web address https://example.com/test?q=hello.world#part-2 and the sentence continues normally.", "https://example.com/test?q=hello.world#part-2", false),
    ("Technical URL longer than max chunk", "Read " + longProtectedUrl + " before continuing.", longProtectedUrl, false),
    ("Technical email longer than max chunk", "Send a report to long.first.last+test-team@some-example-domain.co.uk before leaving.", "long.first.last+test-team@some-example-domain.co.uk", false),
    ("Technical Windows path near boundary", "Please review the folder and its contents at C:\\Users\\Test\\App.v2\\data.json before the next build.", @"C:\Users\Test\App.v2\data.json", false),
    ("URL followed by whitespace near boundary", "Please review this integration carefully at https://example.com/path?a=1 before the next build.", "https://example.com/path?a=1", false),
    ("Email followed by whitespace near boundary", "Please review this integration carefully at test.user+tts@example.co.uk before the next build.", "test.user+tts@example.co.uk", false)
};

bool failuresOnly = args.Any(argument =>
    argument.Equals("--failures-only", StringComparison.OrdinalIgnoreCase));

string? categoryFilter = args.FirstOrDefault(argument =>
    argument.StartsWith("--category=", StringComparison.OrdinalIgnoreCase));
if (categoryFilter is not null)
{
    categoryFilter = categoryFilter["--category=".Length..];
}

var allCases = cases.Select(test => (
        test.Category, test.Name, test.Input, test.Expected,
        Finished: Enumerable.Repeat(true, test.Expected.Length).ToArray()))
    .Concat(behaviorCases);

var selectedCases = string.IsNullOrWhiteSpace(categoryFilter)
    ? allCases.ToArray()
    : allCases.Where(test => test.Category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase)).ToArray();

bool selectedEmergencyCases = string.IsNullOrWhiteSpace(categoryFilter) ||
    categoryFilter.Equals("Long technical boundaries", StringComparison.OrdinalIgnoreCase);

bool selectedRulesConfiguration = string.IsNullOrWhiteSpace(categoryFilter) ||
    categoryFilter.Equals("Custom rules configuration", StringComparison.OrdinalIgnoreCase);

var additionalCases = AdditionalChecks.Cases().ToArray();
var selectedAdditionalCases = additionalCases.Where(test => string.IsNullOrWhiteSpace(categoryFilter) ||
    test.Category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase)).ToArray();

if (selectedCases.Length == 0 &&
    !selectedEmergencyCases && !selectedRulesConfiguration && selectedAdditionalCases.Length == 0 &&
    !string.Equals(categoryFilter, "Early split boundaries", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"No tests found for category: {categoryFilter}");
    Console.Error.WriteLine($"Available categories: {string.Join(", ", allCases.Select(test => test.Category).Append("Long technical boundaries").Append("Custom rules configuration").Append("Early split boundaries").Concat(additionalCases.Select(test => test.Category)).Distinct())}");
    return 2;
}

int failures = 0;
var categoryResults = new Dictionary<string, (int Passed, int Total)>();

foreach (var test in selectedCases)
{
    var chunks = chunker.Split(test.Input, earlySplit: false);
    var actual = chunks.Select(chunk => chunk.Text).ToArray();
    bool textsMatch = actual.SequenceEqual(test.Expected);
    bool finalFlagsMatch = chunks.Select(chunk => chunk.IsSentenceFinished).SequenceEqual(test.Finished);
    bool passed = textsMatch && finalFlagsMatch;

    categoryResults.TryGetValue(test.Category, out var result);
    categoryResults[test.Category] = (result.Passed + (passed ? 1 : 0), result.Total + 1);

    if (passed)
    {
        if (!failuresOnly)
        {
            Console.WriteLine($"PASS [{test.Category}] {test.Name}");
        }

        continue;
    }

    failures++;
    Console.Error.WriteLine($"FAIL [{test.Category}] {test.Name}");
    Console.Error.WriteLine($"  Input:    {test.Input.Replace("\r", "\\r").Replace("\n", "\\n")}");
    Console.Error.WriteLine($"  Expected: {string.Join(" | ", test.Expected)}");
    Console.Error.WriteLine($"  Actual:   {string.Join(" | ", actual)}");
    Console.Error.WriteLine($"  Flags expected: {string.Join(", ", test.Finished)}");
    Console.Error.WriteLine($"  Flags actual:   {string.Join(", ", chunks.Select(chunk => chunk.IsSentenceFinished))}");
}

// An emergency cut may occur on punctuation, whitespace, or a hard size limit,
// but a URL/address/path must appear wholly within a single resulting chunk.
if (selectedEmergencyCases)
{
    const string category = "Long technical boundaries";
    var boundedChunker = new TextChunker(new ChunkerSettings { MaxChunkLength = 80 });
    int passedCount = 0;

    foreach (var test in emergencyCases)
    {
        var chunks = boundedChunker.Split(test.Input, earlySplit: test.EarlySplit);
        bool passed = chunks.Count > 1 &&
                      chunks.Count(chunk => chunk.Text.Contains(test.Protected, StringComparison.Ordinal)) == 1 &&
                      !chunks.Any(chunk => chunk.Text.Contains(test.Protected + "-", StringComparison.Ordinal));
        if (passed)
        {
            passedCount++;
            if (!failuresOnly) Console.WriteLine($"PASS [{category}] {test.Name}");
            continue;
        }

        failures++;
        Console.Error.WriteLine($"FAIL [{category}] {test.Name}");
        Console.Error.WriteLine($"  Protected: {test.Protected}");
        Console.Error.WriteLine($"  Actual:    {string.Join(" | ", chunks.Select(chunk => chunk.Text))}");
    }

    categoryResults[category] = (passedCount, emergencyCases.Length);
}

if (string.IsNullOrWhiteSpace(categoryFilter) ||
    categoryFilter.Equals("Early split boundaries", StringComparison.OrdinalIgnoreCase))
{
    const string category = "Early split boundaries";
    var earlyCases = new (string Name, string Input, string[] Texts, bool[] Finished)[]
    {
        ("Only the first sentence gets an early split", "First, continues. Second, stays together.",
            ["First,", "continues.", "Second, stays together."], [false, true, true]),
        ("Title survives the first clause cut", "St. Olav, spoke. Next.",
            ["St. Olav,", "spoke.", "Next."], [false, true, true]),
        ("Greek question keeps its completion flag", "Είναι σωστό; Ναι, είναι.",
            ["Είναι σωστό;", "Ναι, είναι."], [true, true]),
        ("Reference and decimal stay in the first clause", "See Fig. 2 at 3.14, then continue. Next.",
            ["See Fig. 2 at 3.14,", "then continue.", "Next."], [false, true, true]),
        ("Unfinished input keeps every chunk unfinished", "First, continues without a terminal",
            ["First,", "continues without a terminal"], [false, false])
    };
    int passedCount = 0;
    foreach (var test in earlyCases)
    {
        var actual = chunker.Split(test.Input, earlySplit: true);
        bool passed = actual.Select(c => c.Text).SequenceEqual(test.Texts) &&
            actual.Select(c => c.IsSentenceFinished).SequenceEqual(test.Finished);
        if (passed)
        {
            passedCount++;
            if (!failuresOnly) Console.WriteLine($"PASS [{category}] {test.Name}");
            continue;
        }

        failures++;
        Console.Error.WriteLine($"FAIL [{category}] {test.Name}");
        Console.Error.WriteLine($"  Actual: {string.Join(" | ", actual.Select(c => $"{c.Text} ({c.IsSentenceFinished})"))}");
    }

    categoryResults[category] = (passedCount, earlyCases.Length);
}

// Each configuration has its own immutable catalog; no global reset is necessary.
if (selectedRulesConfiguration)
{
    const string category = "Custom rules configuration";
    int rulesTotal = 0;
    int rulesPassed = 0;

    void CheckRules(string name, bool passed)
    {
        rulesTotal++;
        if (passed)
        {
            rulesPassed++;
            if (!failuresOnly) Console.WriteLine($"PASS [{category}] {name}");
            return;
        }

        failures++;
        Console.Error.WriteLine($"FAIL [{category}] {name}");
    }

    string path = Path.Combine(Path.GetTempPath(), $"tsubaki-rules-{Guid.NewGuid():N}.json");
    try
    {
        var defaults = TextChunkerRules.LoadOrDefault(path);
        CheckRules("Absent JSON uses built-in rules without creating a file",
            ReferenceEquals(defaults, TextChunkerRules.Default) && !File.Exists(path));

        File.WriteAllText(path, "{}");
        var empty = new TextChunker(new ChunkerSettings(), TextChunkerRules.LoadOrDefault(path));
        CheckRules("Empty JSON preserves built-in sentence behavior", empty.Split("St. Olav spoke. Next.")
            .Select(c => c.Text).SequenceEqual(["St. Olav spoke.", "Next."]));

        File.WriteAllText(path, """
            {
              "AdditionalSentenceTerminators": ["⁖", "⁖"],
              "AdditionalClausePunctuation": ["⁏"],
              "AdditionalPauseMarks": ["※"],
              "AdditionalClosingPunctuation": ["⟫"],
              "AdditionalAbbreviations": ["zzz.", "ZZZ．"],
              "AdditionalPrefixAbbreviations": ["Archon."],
              "AdditionalNameBindingAbbreviations": ["sv.", "м."],
              "AdditionalNumberBindingAbbreviations": ["eqn."],
              "AdditionalIntroductoryAbbreviations": ["i.ex."]
            }
            """);
        var custom = TextChunkerRules.LoadOrDefault(path);
        var customChunker = new TextChunker(new ChunkerSettings(), custom);
        CheckRules("Built-in abbreviations preserved", custom.CommonAbbreviations.Contains("dr"));
        CheckRules("Extra abbreviations normalized and deduplicated", custom.CommonAbbreviations.Contains("zzz") &&
            custom.CommonAbbreviations.Count(word => word.Equals("zzz", StringComparison.OrdinalIgnoreCase)) == 1 &&
            custom.CommonAbbreviations.Contains("archon") && custom.CommonAbbreviations.Contains("м"));
        CheckRules("User sentence terminator", customChunker.Split("Hello⁖ Next sentence.")
            .Select(c => c.Text).SequenceEqual(["Hello⁖", "Next sentence."]));
        CheckRules("User prefix binds to a name", customChunker.Split("Archon. Morgan arrived.")
            .Select(c => c.Text).SequenceEqual(["Archon. Morgan arrived."]));
        CheckRules("Configured lowercase abbreviation protects a name", customChunker.Split("м. Київ працює. Далі.")
            .Select(c => c.Text).SequenceEqual(["м. Київ працює.", "Далі."]));
        CheckRules("User numbered reference stays together", customChunker.Split("See eqn. 2. Continue.")
            .Select(c => c.Text).SequenceEqual(["See eqn. 2.", "Continue."]));
        CheckRules("User introductory abbreviation takes a proper name", customChunker.Split("Use i.ex. Python here. Next.")
            .Select(c => c.Text).SequenceEqual(["Use i.ex. Python here.", "Next."]));
        CheckRules("User clause enables early split", customChunker.Split("Hello⁏ world.", earlySplit: true)
            .Select(c => c.Text).SequenceEqual(["Hello⁏", "world."]));
        CheckRules("User closing punctuation extends boundary", customChunker.Split("Hello?⟫ Next.")
            .Select(c => c.Text).SequenceEqual(["Hello?⟫", "Next."]));
        CheckRules("Default catalog is unaffected by another configuration",
            !TextChunkerRules.Default.CommonAbbreviations.Contains("zzz") &&
            !TextChunkerRules.Default.SentenceTerminators.Contains('⁖') &&
            chunker.Split("м. Київ працює. Далі.").Select(c => c.Text)
                .SequenceEqual(["м.", "Київ працює.", "Далі."]));

        bool concurrent = Enumerable.Range(0, 40).AsParallel().All(_ =>
            customChunker.Split("м. Київ працює. Далі.").Count == 2 &&
            chunker.Split("м. Київ працює. Далі.").Count == 3);
        CheckRules("Independent catalogs remain consistent across concurrent requests", concurrent);

        File.WriteAllText(path, "{\"AdditionalPeriodLikeMarks\":[\"⁖\"]}");
        var periodChunker = new TextChunker(new ChunkerSettings(), TextChunkerRules.LoadOrDefault(path));
        CheckRules("Custom period-like marks use abbreviation and lowercase-letter rules",
            periodChunker.Split("Dr⁖ Smith spoke. The letter is z⁖ Continue.").Select(c => c.Text)
                .SequenceEqual(["Dr⁖ Smith spoke.", "The letter is z⁖", "Continue."]));

        File.WriteAllText(path, "{\"AdditionalEllipsisMarks\":[\"⁖\"]}");
        var ellipsisChunker = new TextChunker(new ChunkerSettings(), TextChunkerRules.LoadOrDefault(path));
        CheckRules("Custom ellipses remain contextual", ellipsisChunker.Split("Wait⁖ maybe. Next.")
            .Select(c => c.Text).SequenceEqual(["Wait⁖ maybe.", "Next."]));

        bool Rejects(string json)
        {
            File.WriteAllText(path, json);
            try
            {
                _ = TextChunkerRules.LoadOrDefault(path);
                return false;
            }
            catch (InvalidDataException)
            {
                return true;
            }
        }

        CheckRules("Global ASCII semicolon is rejected", Rejects("{\"AdditionalSentenceTerminators\":[\";\"]}"));
        CheckRules("Conflicting categories are rejected", Rejects("{\"AdditionalSentenceTerminators\":[\",\"]}"));
        CheckRules("Invalid JSON fails explicitly", Rejects("{ invalid"));
        CheckRules("Unknown category fails explicitly", Rejects("{\"UnknownRules\":[]}"));
        CheckRules("Invalid punctuation entry fails explicitly", Rejects("{\"AdditionalSentenceTerminators\":[\"abc\"]}"));
        CheckRules("Conflicting punctuation semantics are rejected", Rejects("{\"AdditionalQuestionMarks\":[\"!\"]}"));
        CheckRules("Invalid abbreviation entry fails explicitly", Rejects("{\"AdditionalAbbreviations\":[\"two words\"]}"));
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }

    categoryResults[category] = (rulesPassed, rulesTotal);
}

foreach (var test in selectedAdditionalCases)
{
    bool passed = false;
    try
    {
        test.Check();
        passed = true;
        if (!failuresOnly) Console.WriteLine($"PASS [{test.Category}] {test.Name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL [{test.Category}] {test.Name}: {error.Message}");
    }
    categoryResults.TryGetValue(test.Category, out var result);
    categoryResults[test.Category] = (result.Passed + (passed ? 1 : 0), result.Total + 1);
}

Console.WriteLine();
foreach (var (category, result) in categoryResults)
{
    Console.WriteLine($"{category}: {result.Passed}/{result.Total}");
}

int totalCases = selectedCases.Length + selectedAdditionalCases.Length +
    (selectedEmergencyCases ? emergencyCases.Length : 0) +
    (selectedRulesConfiguration ? categoryResults["Custom rules configuration"].Total : 0) +
    (categoryResults.TryGetValue("Early split boundaries", out var earlyResult) ? earlyResult.Total : 0);
Console.WriteLine($"Result: {totalCases - failures}/{totalCases} passed");
return failures == 0 ? 0 : 1;
