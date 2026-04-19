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
                ["German"] = "German",
                // Validation
                ["PasswordMinLength"] = "Password must be at least {0} characters long.",
                ["PasswordMismatch"] = "The password and confirmation password do not match.",
                ["EmailRequired"] = "Email is required.",
                ["EmailInvalid"] = "Invalid email format.",
                ["PasswordRequired"] = "Password is required.",
                ["ConfirmPasswordRequired"] = "Please confirm your password.",
                // Forgot password
                ["ForgotPassword"] = "Forgot password?",
                ["ForgotPasswordTitle"] = "Password Recovery",
                ["ForgotPasswordIntro"] = "Enter your email address and we will send you a new password.",
                ["SendNewPassword"] = "Send new password",
                ["BackToLogin"] = "Back to login",
                ["EmailNotRegistered"] = "This email is not registered in our system.",
                ["NewPasswordSent"] = "A new password has been sent to your email. Please check your inbox.",
                ["EmailSendFailed"] = "Failed to send the email. Please try again later.",
                // Email body
                ["EmailSubject"] = "Your new MedicalApp password",
                ["EmailGreeting"] = "Hello,",
                ["EmailNewPasswordIntro"] = "Your new password for MedicalApp is:",
                ["EmailChangeAdvice"] = "For security reasons, please log in and change this password immediately.",
                ["EmailRegards"] = "Regards, MedicalApp Team"
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
                ["German"] = "Germană",
                // Validation
                ["PasswordMinLength"] = "Parola trebuie să aibă cel puțin {0} caractere.",
                ["PasswordMismatch"] = "Parola și confirmarea parolei nu coincid.",
                ["EmailRequired"] = "Email-ul este obligatoriu.",
                ["EmailInvalid"] = "Format email invalid.",
                ["PasswordRequired"] = "Parola este obligatorie.",
                ["ConfirmPasswordRequired"] = "Te rugăm să confirmi parola.",
                // Forgot password
                ["ForgotPassword"] = "Ai uitat parola?",
                ["ForgotPasswordTitle"] = "Recuperare parolă",
                ["ForgotPasswordIntro"] = "Introdu adresa de email și îți vom trimite o parolă nouă.",
                ["SendNewPassword"] = "Trimite parola nouă",
                ["BackToLogin"] = "Înapoi la autentificare",
                ["EmailNotRegistered"] = "Acest email nu este înregistrat în sistem.",
                ["NewPasswordSent"] = "O parolă nouă a fost trimisă pe email. Verifică inbox-ul.",
                ["EmailSendFailed"] = "Trimiterea email-ului a eșuat. Te rugăm să încerci mai târziu.",
                // Email body
                ["EmailSubject"] = "Noua ta parolă MedicalApp",
                ["EmailGreeting"] = "Salut,",
                ["EmailNewPasswordIntro"] = "Noua ta parolă pentru MedicalApp este:",
                ["EmailChangeAdvice"] = "Pentru siguranță, te rugăm să te autentifici și să schimbi această parolă imediat.",
                ["EmailRegards"] = "Cu respect, Echipa MedicalApp"
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
                ["German"] = "Allemand",
                // Validation
                ["PasswordMinLength"] = "Le mot de passe doit contenir au moins {0} caractères.",
                ["PasswordMismatch"] = "Le mot de passe et la confirmation ne correspondent pas.",
                ["EmailRequired"] = "L'email est obligatoire.",
                ["EmailInvalid"] = "Format d'email invalide.",
                ["PasswordRequired"] = "Le mot de passe est obligatoire.",
                ["ConfirmPasswordRequired"] = "Veuillez confirmer votre mot de passe.",
                // Forgot password
                ["ForgotPassword"] = "Mot de passe oublié ?",
                ["ForgotPasswordTitle"] = "Récupération du mot de passe",
                ["ForgotPasswordIntro"] = "Entrez votre adresse email et nous vous enverrons un nouveau mot de passe.",
                ["SendNewPassword"] = "Envoyer un nouveau mot de passe",
                ["BackToLogin"] = "Retour à la connexion",
                ["EmailNotRegistered"] = "Cet email n'est pas enregistré dans notre système.",
                ["NewPasswordSent"] = "Un nouveau mot de passe a été envoyé à votre email. Veuillez vérifier votre boîte de réception.",
                ["EmailSendFailed"] = "L'envoi de l'email a échoué. Veuillez réessayer plus tard.",
                // Email body
                ["EmailSubject"] = "Votre nouveau mot de passe MedicalApp",
                ["EmailGreeting"] = "Bonjour,",
                ["EmailNewPasswordIntro"] = "Votre nouveau mot de passe pour MedicalApp est :",
                ["EmailChangeAdvice"] = "Pour des raisons de sécurité, veuillez vous connecter et changer ce mot de passe immédiatement.",
                ["EmailRegards"] = "Cordialement, L'équipe MedicalApp"
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
                ["German"] = "Alemán",
                // Validation
                ["PasswordMinLength"] = "La contraseña debe tener al menos {0} caracteres.",
                ["PasswordMismatch"] = "La contraseña y la confirmación no coinciden.",
                ["EmailRequired"] = "El correo es obligatorio.",
                ["EmailInvalid"] = "Formato de correo inválido.",
                ["PasswordRequired"] = "La contraseña es obligatoria.",
                ["ConfirmPasswordRequired"] = "Por favor confirme su contraseña.",
                // Forgot password
                ["ForgotPassword"] = "¿Olvidó su contraseña?",
                ["ForgotPasswordTitle"] = "Recuperación de contraseña",
                ["ForgotPasswordIntro"] = "Introduzca su correo y le enviaremos una nueva contraseña.",
                ["SendNewPassword"] = "Enviar nueva contraseña",
                ["BackToLogin"] = "Volver al inicio de sesión",
                ["EmailNotRegistered"] = "Este correo no está registrado en nuestro sistema.",
                ["NewPasswordSent"] = "Se ha enviado una nueva contraseña a su correo. Por favor, revise su bandeja de entrada.",
                ["EmailSendFailed"] = "Error al enviar el correo. Por favor, inténtelo más tarde.",
                // Email body
                ["EmailSubject"] = "Su nueva contraseña de MedicalApp",
                ["EmailGreeting"] = "Hola,",
                ["EmailNewPasswordIntro"] = "Su nueva contraseña para MedicalApp es:",
                ["EmailChangeAdvice"] = "Por razones de seguridad, inicie sesión y cambie esta contraseña inmediatamente.",
                ["EmailRegards"] = "Saludos, Equipo MedicalApp"
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
                ["German"] = "Deutsch",
                // Validation
                ["PasswordMinLength"] = "Das Passwort muss mindestens {0} Zeichen lang sein.",
                ["PasswordMismatch"] = "Passwort und Bestätigung stimmen nicht überein.",
                ["EmailRequired"] = "E-Mail ist erforderlich.",
                ["EmailInvalid"] = "Ungültiges E-Mail-Format.",
                ["PasswordRequired"] = "Passwort ist erforderlich.",
                ["ConfirmPasswordRequired"] = "Bitte bestätigen Sie Ihr Passwort.",
                // Forgot password
                ["ForgotPassword"] = "Passwort vergessen?",
                ["ForgotPasswordTitle"] = "Passwort-Wiederherstellung",
                ["ForgotPasswordIntro"] = "Geben Sie Ihre E-Mail-Adresse ein und wir senden Ihnen ein neues Passwort.",
                ["SendNewPassword"] = "Neues Passwort senden",
                ["BackToLogin"] = "Zurück zur Anmeldung",
                ["EmailNotRegistered"] = "Diese E-Mail ist in unserem System nicht registriert.",
                ["NewPasswordSent"] = "Ein neues Passwort wurde an Ihre E-Mail gesendet. Bitte überprüfen Sie Ihren Posteingang.",
                ["EmailSendFailed"] = "Das Senden der E-Mail ist fehlgeschlagen. Bitte versuchen Sie es später erneut.",
                // Email body
                ["EmailSubject"] = "Ihr neues MedicalApp-Passwort",
                ["EmailGreeting"] = "Hallo,",
                ["EmailNewPasswordIntro"] = "Ihr neues Passwort für MedicalApp lautet:",
                ["EmailChangeAdvice"] = "Aus Sicherheitsgründen melden Sie sich bitte an und ändern Sie dieses Passwort sofort.",
                ["EmailRegards"] = "Mit freundlichen Grüßen, das MedicalApp-Team"
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
