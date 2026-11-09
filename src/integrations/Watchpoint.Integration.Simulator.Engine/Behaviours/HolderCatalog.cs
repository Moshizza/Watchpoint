namespace Watchpoint.Integration.Simulator.Engine.Behaviours;

public static class HolderCatalog
{
    public static readonly IReadOnlyList<string> Names =
    [
        "Alice Smith", "Bob Jones", "فاطمة الزهراء", "محمد بن علي", "Éléonore Mercier",
        "François Dupont", "Müller Hans", "Jürgen Schmidt", "عمر الفاروق", "يوسف أحمد",
        "Amina Benali", "Chloé Lefèvre", "Søren Kierkegaard", "José García", "María Rodríguez",
        "خالد المنصور", "ليلى السعيد", "Astrid Lindgren", "Noémie Dubois", "Benoît Blanc",
        "Hassan Al-Sabah", "Zainab Qasim", "Camille Martin", "Aurélie Moreau", "Gérard Depardieu",
        "طارق زياد", "منى عبد الله", "Hanna Andersson", "Carlos Santana", "Inès Benmansour",
        "Guillaume Tell", "Stéphane Mallarmé", "نور الهدى", "مصطفى كامل", "Sébastien Loeb",
        "Valérie Pécresse", "Ahmed Zewail", "نجيب محفوظ", "Álvaro Morte", "Éric Cantona",
        "Salma Hayek", "كريم بنزيما", "حمزة يوسف", "Béatrice Dalle", "Matthieu Chedid",
        "Théo Hernandez", "سميرة موسى", "بلال فضل", "René Descartes", "Blaise Pascal",
        "فاروق الباز", "جمال عبد الناصر", "Jeanne d'Arc", "Loïc Nottet", "Gaël Monfils",
        "هدى شعراوي", "سعاد حسني", "Cécile de France", "Thierry Henry", "Adèle Exarchopoulos",
        "ياسين بونو", "أشرف حكيمي", "Kylian Mbappé", "Zinédine Zidane", "Lilian Thuram",
        "أم كلثوم", "فيروز حداد", "Gilles Deleuze", "Michel Foucault", "Simone de Beauvoir",
        "طه حسين", "عباس العقاد", "Jean-Paul Sartre", "Albert Camus", "Marcel Proust",
        "نزار قباني", "محمود درويش", "Victor Hugo", "Émile Zola", "Gustave Flaubert",
        "غسان كنفاني", "إدوارد سعيد", "Arthur Rimbaud", "Paul Verlaine", "Charles Baudelaire",
        "مروان الشماخ", "سفيان أمرابط", "Lucas Hernandez", "Antoine Griezmann", "Olivier Giroud",
        "حاتم الطائي", "عنترة بن شداد", "Pierre Curie", "Marie Curie", "Louis Pasteur",
        "ابن سينا", "ابن رشد", "Henri Poincaré", "Laurent Schwartz", "Cédric Villani",
        "الخوارزمي", "البيروني", "Claude Shannon", "Alan Turing", "Ada Lovelace",
        "جابر بن حيان", "الحسن بن الهيثم", "David Hilbert", "Emmy Noether", "Kurt Gödel",
        "إبراهيم طوقان", "فدوى طوقان", "Jacques Brel", "Édith Piaf", "Charles Aznavour",
        "أحمد شوقي", "حافظ إبراهيم", "Georges Brassens", "Serge Gainsbourg", "Léo Ferré",
        "صلاح الدين الأيوبي", "سيف الدين قطز", "Napoleon Bonaparte", "Jean Jaurès", "Charles de Gaulle",
        "الظاهر بيبرس", "شجر الدر", "Georges Clemenceau", "Léon Blum", "François Mitterrand",
        "محمد عبده", "جمال الدين الأفغاني", "Jacques Chirac", "Nicolas Sarkozy", "Emmanuel Macron",
        "عبد الرحمن الكواكبي", "رشيد رضا", "Jean Moulin", "Lucie Aubrac", "Raymond Aubrac",
        "علي عزت بيغوفيتش", "مهاتير محمد", "Nelson Mandela", "Desmond Tutu", "Kofi Annan",
        "عبد القادر الجزائري", "العربي بن مهيدي", "Aimé Césaire", "Frantz Fanon", "Léopold Sédar Senghor",
        "مصطفى بن بولعيد", "ديدوش مراد", "Tahar Ben Jelloun", "Amin Maalouf", "Kateb Yacine",
        "حسيبة بن بوعلي", "جميلة بوحيرد", "Assia Djebar", "Leïla Slimani", "Yasmina Khadra",
        "أحمد بن بلة", "هواري بومدين", "Alain Delon", "Jean-Paul Belmondo", "Catherine Deneuve",
        "محمد بوضياف", "حسين آيت أحمد", "Sophie Marceau", "Marion Cotillard", "Juliette Binoche",
        "علال الفاسي", "محمد الخامس", "Jean Reno", "Vincent Cassel", "Gérard Philipe",
        "عبد الكريم الخطابي", "المهدي بن بركة", "Louis de Funès", "Bourvil", "Fernandel",
        "عمر المختار", "إدريس السنوسي", "Coluche", "Pierre Desproges", "Raymond Devos",
        "الحبيب بورقيبة", "عبد العزيز الثعالبي", "Molière", "Jean Racine", "Pierre Corneille",
        "فرحات حشاد", "صالح بن يوسف", "Voltaire", "Jean-Jacques Rousseau", "Denis Diderot",
        "مالك بن نبي", "مولود معمري", "Montesquieu", "Alexis de Tocqueville", "Henri Bergson",
    ];

    public static (string holderRef, string holderName) GetHolder(int index)
    {
        int bounded = Math.Abs(index) % Names.Count;
        return ($"H-{bounded + 1:D4}", Names[bounded]);
    }
}
