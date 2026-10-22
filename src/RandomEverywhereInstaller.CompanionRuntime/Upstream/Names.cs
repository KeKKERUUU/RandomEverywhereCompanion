// HPG Mission Companion - static lists of first names (no surnames) for the crew and the technicians: they are the first names the HEMS mission
// gives to its patients (mission-src data malenames and femalenames). With the option "Random names for each shift" each crew role and each technician
// takes one name from these lists at the start of a shift. See docs/architecture/VOICEPACKS.md.
namespace HPGMissionCompanion
{
    internal static class NameLists
    {
        public static readonly string[] Male = new string[]
        {
            "Wade", "Dave", "Seth", "Ivan", "Riley", "Gilbert", "Jorge", "Dan", "Brian", "Roberto", "Ramon", "Miles", "Liam", "Nathaniel", "Ethan",
            "Lewis", "Milton", "Claude", "Joshua", "Glen", "Harvey", "Blake", "Antonio", "Connor", "Vincent", "Sean", "Derek", "Victor", "Oscar",
            "Edgar", "Luke", "Peter", "Frederick", "Maxwell", "Neil", "Barry", "Mitchell", "Arthur", "Colin", "Simon", "Henry", "Frank", "Leonard",
            "Gavin", "Harry", "Wayne", "Samuel", "Dylan", "Quentin", "Brett", "Patrick", "Alan", "Larry", "Kenneth", "Timothy", "Roger", "Stuart",
            "Ronald", "Dean", "Albert", "Bradley", "Marcus", "Dennis", "George", "Walter", "Dwight", "Phillip", "Martin", "Eugene", "Glenn",
            "Herbert", "Clarence", "Xavier", "Desmond", "Julian", "Ian", "Kurt", "Carl", "Jerome", "Travis", "Shane", "Russell", "Curtis",
            "Terrence", "Felix", "Edwin", "Malcolm", "Louis", "Raymond", "Bryce", "Dominic", "Marshall", "Hector", "Theodore", "Nolan", "Stanley",
            "Calvin", "Corey", "Jordan", "Dustin", "Jarvis", "Dale", "Alberto", "Gregory", "Tommy", "Garrett", "Gerald", "Mason", "Harold", "Ralph",
            "Mario", "Landon", "Fernando", "Jeremiah", "Everett", "Malachi", "Joel", "Byron", "Damian", "Clifford", "Ricardo", "Tristan", "Javier",
            "Fabian", "Jonah", "Dominick", "Trevor", "Spencer", "Brent", "Brendan", "Weston", "Conrad", "Kendrick", "Giovanni", "Morgan", "Marvin",
            "Ezekiel", "Orlando", "Brock", "Andre", "Isaiah", "Damon", "Levi", "Wesley", "Bryant", "Jayson", "Jarrett", "Reginald", "Hugo",
            "Leonardo", "Roderick", "Harley", "Griffin", "Julius", "Sylvester", "Rory", "Roland", "Rudy", "Kristopher", "Quinton", "Elliott",
            "Jasper", "Dexter", "Malik", "Lane", "Noel", "Emmanuel", "Kellan", "Kingston", "Jagger", "Maximilian", "Harrison", "Orion", "Reuben",
            "Finnegan", "Brennan", "Beckett", "Caleb", "Xander", "Tobias", "Nash", "Atticus", "Luka", "Jett", "Branson", "Kieran", "Augustus",
            "Brody", "Killian", "Kyler", "Callum", "Grady", "Enzo", "Cason", "Rhett", "Archer", "Colton", "Jensen", "Remy", "Sawyer", "Zayden",
            "Daxton", "Jaxton", "Ronan", "Bentley", "Cohen", "Keegan", "Maddox", "Gunner", "Silas", "Knox", "Kohen", "Zane", "Lennox", "Jax",
            "Atlas", "Beckham", "Dawson", "Ryker", "Wilder", "Crew", "Phoenix",
        };

        public static readonly string[] Female = new string[]
        {
            "Daisy", "Deborah", "Isabel", "Stella", "Debra", "Beverly", "Vera", "Angela", "Lucy", "Lauren", "Janet", "Loretta", "Tracey", "Beatrice",
            "Sabrina", "Melody", "Chrysta", "Christina", "Vicki", "Molly", "Alison", "Miranda", "Stephanie", "Leona", "Katrina", "Ava", "Sophia",
            "Olivia", "Emma", "Isabella", "Mia", "Evelyn", "Harper", "Camila", "Gianna", "Abigail", "Luna", "Avery", "Mila", "Sofia", "Chloe",
            "Charlotte", "Amelia", "Ella", "Scarlett", "Emily", "Madison", "Layla", "Lily", "Zoey", "Nora", "Penelope", "Aria", "Grace", "Ellie",
            "Aubrey", "Zoe", "Elizabeth", "Sophie", "Natalie", "Hannah", "Aaliyah", "Leah", "Madelyn", "Samantha", "Ariana", "Mackenzie", "Victoria",
            "Kinsley", "Lillian", "Addison", "Hazel", "Nova", "Emilia", "Violet", "Willow", "Aurora", "Emery", "Amara", "Eleanor", "Alice",
            "Madeline", "Harmony", "Athena", "Eva", "Quinn", "Sadie", "Piper", "Ruby", "Lydia", "Eliana", "Josephine", "Delilah", "Isla", "Lyla",
            "Valentina", "Elise", "Liliana", "Payton", "Katherine", "Adalyn", "Giuliana", "Elena", "Giselle", "Ivy", "Jade", "Maria", "Paris",
            "Rachel", "Summer", "Tessa", "Vanessa", "Zara", "Brianna", "Cecilia", "Daniella", "Fiona", "Hope", "Jasmine", "Kiara", "Melanie",
            "Naomi", "Savannah", "Zuri", "Audrey", "Brooklyn",
        };
    }
}
