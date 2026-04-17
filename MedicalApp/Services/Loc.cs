using System.Globalization;

namespace MedicalApp.Services
{
    /// <summary>
    /// Simple in-memory localizer for the MedicalApp.
    /// Use @Loc.T("KeyName") in Razor views or Loc.T("KeyName") in controllers.
    /// </summary>
    public static class Loc
    {
        private static readonly Dictionary<string, Dictionary<string, string>> _translations = new()
        {
            ["en"] = new()
            {
                ["AppTitle"] = "MedicalApp",
                ["WelcomeMessage"] = "Welcome in this world",
                ["SelectLanguage"] = "Select language",
                ["Login"] = "Login",
                ["Register"] = "Register",
                ["Email"] = "Email",
                ["Password"] = "Password",
                ["ConfirmPassword"] = "Confirm Password",
                ["LoginButton"] = "Sign In",
                ["RegisterButton"] = "Create Account",
                ["Logout"] = "Logout",
                ["InvalidCredentials"] = "Invalid email or password.",
                ["EmailAlreadyExists"] = "This email is already registered.",
                ["RegistrationSuccess"] = "Account created successfully. You can now login.",
                ["WelcomeBack"] = "Welcome back",
                ["Credits"] = "Credits",
                ["English"] = "English",
                ["Romanian"] = "Romanian",
                ["French"] = "French",
                ["Spanish"] = "Spanish",
                ["German"] = "German"
            },
            ["ro"] = new()
            {
                ["AppTitle"] = "MedicalApp",
                ["WelcomeMessage"] = "Bine ai venit în această lume",
                ["SelectLanguage"] = "Selectează limba",
                ["Login"] = "Autentificare",
                ["Register"] = "Înregistrare",
                ["Email"] = "Email",
                ["Password"] = "Parolă",
                ["ConfirmPassword"] = "Confirmă parola",
                ["LoginButton"] = "Autentifică-te",
                ["RegisterButton"] = "Creează cont",
                ["Logout"] = "Deconectare",
                ["InvalidCredentials"] = "Email sau parolă incorecte.",
                ["EmailAlreadyExists"] = "Acest email este deja înregistrat.",
                ["RegistrationSuccess"] = "Cont creat cu succes. Te poți autentifica acum.",
                ["WelcomeBack"] = "Bine ai revenit",
                ["Credits"] = "Credite",
                ["English"] = "Engleză",
                ["Romanian"] = "Română",
                ["French"] = "Franceză",
                ["Spanish"] = "Spaniolă",
                ["German"] = "Germană"
            },
            ["fr"] = new()
            {
                ["AppTitle"] = "MedicalApp",
                ["WelcomeMessage"] = "Bienvenue dans ce monde",
                ["SelectLanguage"] = "Choisir la langue",
                ["Login"] = "Connexion",
                ["Register"] = "S'inscrire",
                ["Email"] = "Email",
                ["Password"] = "Mot de passe",
                ["ConfirmPassword"] = "Confirmer le mot de passe",
                ["LoginButton"] = "Se connecter",
                ["RegisterButton"] = "Créer un compte",
                ["Logout"] = "Déconnexion",
                ["InvalidCredentials"] = "Email ou mot de passe invalide.",
                ["EmailAlreadyExists"] = "Cet email est déjà enregistré.",
                ["RegistrationSuccess"] = "Compte créé avec succès. Vous pouvez vous connecter.",
                ["WelcomeBack"] = "Bon retour",
                ["Credits"] = "Crédits",
                ["English"] = "Anglais",
                ["Romanian"] = "Roumain",
                ["French"] = "Français",
                ["Spanish"] = "Espagnol",
                ["German"] = "Allemand"
            },
            ["es"] = new()
            {
                ["AppTitle"] = "MedicalApp",
                ["WelcomeMessage"] = "Bienvenido a este mundo",
                ["SelectLanguage"] = "Seleccionar idioma",
                ["Login"] = "Iniciar sesión",
                ["Register"] = "Registrarse",
                ["Email"] = "Correo",
                ["Password"] = "Contraseña",
                ["ConfirmPassword"] = "Confirmar contraseña",
                ["LoginButton"] = "Entrar",
                ["RegisterButton"] = "Crear cuenta",
                ["Logout"] = "Cerrar sesión",
                ["InvalidCredentials"] = "Correo o contraseña inválidos.",
                ["EmailAlreadyExists"] = "Este correo ya está registrado.",
                ["RegistrationSuccess"] = "Cuenta creada con éxito. Ya puede iniciar sesión.",
                ["WelcomeBack"] = "Bienvenido de nuevo",
                ["Credits"] = "Créditos",
                ["English"] = "Inglés",
                ["Romanian"] = "Rumano",
                ["French"] = "Francés",
                ["Spanish"] = "Español",
                ["German"] = "Alemán"
            },
            ["de"] = new()
            {
                ["AppTitle"] = "MedicalApp",
                ["WelcomeMessage"] = "Willkommen in dieser Welt",
                ["SelectLanguage"] = "Sprache auswählen",
                ["Login"] = "Anmelden",
                ["Register"] = "Registrieren",
                ["Email"] = "E-Mail",
                ["Password"] = "Passwort",
                ["ConfirmPassword"] = "Passwort bestätigen",
                ["LoginButton"] = "Einloggen",
                ["RegisterButton"] = "Konto erstellen",
                ["Logout"] = "Abmelden",
                ["InvalidCredentials"] = "Ungültige E-Mail oder Passwort.",
                ["EmailAlreadyExists"] = "Diese E-Mail ist bereits registriert.",
                ["RegistrationSuccess"] = "Konto erfolgreich erstellt. Sie können sich jetzt anmelden.",
                ["WelcomeBack"] = "Willkommen zurück",
                ["Credits"] = "Credits",
                ["English"] = "Englisch",
                ["Romanian"] = "Rumänisch",
                ["French"] = "Französisch",
                ["Spanish"] = "Spanisch",
                ["German"] = "Deutsch"
            }
        };

        public static string T(string key)
        {
            var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (_translations.TryGetValue(culture, out var dict) && dict.TryGetValue(key, out var val))
                return val;
            if (_translations["en"].TryGetValue(key, out var fallback))
                return fallback;
            return key;
        }
    }
}
