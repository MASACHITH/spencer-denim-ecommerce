import { GuideSection } from '../guide.model';

/**
 * Access and security: signing in, two-factor authentication, staff users, roles and permissions. Read from
 * the identity code, the admin screens and the seeded role definitions. Where the project's design documents
 * promise something the code does not do (role editing, password resets, break-glass recovery), this
 * describes the code.
 */
export const ACCESS_SECTIONS: readonly GuideSection[] = [
  {
    id: 'sign-in',
    group: 'Access and security',
    title: 'Signing in, passwords and staying signed in',
    summary:
      'How to sign in, the password rules, what locks an account, how long a session lasts, and what to do if you are locked out or have forgotten your password.',
    screens: [
      { label: 'Change password', path: '/change-password' },
      { label: 'Account security', path: '/security' },
    ],
    blocks: [
      {
        kind: 'steps',
        title: 'Sign in',
        items: [
          'Open the admin address your developer gave you. You see "Sign in" with **Email** and **Password** boxes.',
          'Type both and press **Sign in**. The button stays inactive until both boxes are filled.',
          'If your account has two-factor authentication, you are asked for a code next (see [Two-factor authentication](#two-factor)).',
          'The first time you sign in with a temporary password you must choose a new one before you can use anything else.',
        ],
      },
      { kind: 'heading', text: 'The password rule' },
      {
        kind: 'text',
        text: 'A password must have **at least 10 characters**, and must mix a capital letter, a small letter, a digit and a symbol. The sign-in screens print the exact rule beside the box, and it is the same for staff and for customers. There is no password expiry, and the system does not keep a history of old passwords.',
      },
      {
        kind: 'steps',
        title: 'Change your password',
        items: [
          'Open [Change password](/change-password). There is no link to it in the menu: you reach it from [Account security](/security), where there is a "Change password" link, or you are sent there automatically when a change is required.',
          'Type your **Current password**, then the **New password** twice (**Confirm new password**). The new password must be different from the current one.',
          'Press **Change password**. Other devices signed in to your account are signed out; this one carries on.',
        ],
      },
      { kind: 'heading', text: 'Locked out, forgotten password, lost phone' },
      {
        kind: 'list',
        items: [
          '**5 wrong passwords or wrong codes in a row lock the account for 15 minutes.** You see "This account is temporarily locked after repeated failed sign-in attempts. Try again later." It applies to everyone, including the only Super Admin, and it always ends by itself. Nothing in the admin unlocks an account earlier.',
          'A wrong email, a deactivated account and a wrong password all show the same message, "Invalid email or password.", so it never reveals which emails belong to staff.',
          '**There is no "forgot password" link for staff and no screen where another user can reset your password.** Ask your developer or technical owner for help.',
          'A lost phone with two-factor authentication: sign in with one of your saved **recovery codes**. If you have none left, ask your developer. Nobody can reset two-factor authentication from the admin.',
        ],
      },
      { kind: 'heading', text: 'How long you stay signed in' },
      {
        kind: 'list',
        items: [
          'You stay signed in while you are working. After **60 minutes with no activity** you are signed out. Closing the browser normally signs you out (not if your browser is set to restore your last session). There is no "remember me".',
          'You are signed out within about a minute if your account is deactivated, and immediately on your next click if your roles, password or two-factor settings change.',
          'After a time-out, opening any page takes you to the sign-in page, and afterwards back to the page you wanted.',
          'To sign out yourself press **Sign out** at the bottom of the left-hand menu. On a phone or small screen, open the menu with the button at the top left.',
        ],
      },
      {
        kind: 'callout',
        tone: 'tip',
        text: 'Our advice, not a system rule: each person should have their own account, never share a password, and sign out on a shared computer.',
      },
    ],
  },
  {
    id: 'two-factor',
    group: 'Access and security',
    title: 'Two-factor authentication',
    summary:
      'A code from your phone asked for at every sign-in. Required for Super Admins, optional for everyone else.',
    screens: [{ label: 'Account security', path: '/security' }],
    blocks: [
      {
        kind: 'text',
        text: 'Every **Super Admin** must use it: until it is set up they can reach nothing else (after any forced password change), and they are sent to the [set-up page](/two-factor/setup) automatically. Other staff can turn it on or off at [Account security](/security). That page is not linked from the menu: open it from here, or type its address.',
      },
      {
        kind: 'steps',
        title: 'Set it up',
        items: [
          'Install an authenticator app on your phone, such as Google Authenticator, Microsoft Authenticator or 1Password.',
          'On the set-up page, scan the QR code with the app. If you cannot scan it, choose "enter a setup key" in the app and type the key shown (the **Copy key** button copies it).',
          'Type the 6-digit **Code from the app** and press **Turn on two-factor authentication**. If you see "That code is not valid. Check the time on your phone and try again.", check your phone\'s clock.',
          'The screen then shows your **recovery codes** once: 10 codes, each used once. Press **Copy codes** or **Download as a file** and keep them somewhere safe, such as a password manager. They are not shown again.',
          'Tick "I have saved my recovery codes" and press **Continue**.',
        ],
      },
      {
        kind: 'list',
        title: 'Signing in with it',
        items: [
          'After your password you are asked for the **Authentication code**: the 6-digit code from the app. Type it and press **Verify**. A code works once.',
          'Lost your phone? Choose **Use a recovery code instead** and type one saved code (with or without the hyphen). Each works once.',
          'The password step stays open for 5 minutes. After that you see "Your sign-in has expired. Enter your email and password again."',
          'A wrong code counts towards the same 5-try lockout. The system asks for a code at every sign-in; there is no "remember this browser".',
        ],
      },
      {
        kind: 'list',
        title: 'On the Account security page',
        items: [
          'It shows whether two-factor is on and how many recovery codes are left. With 2 or fewer you are warned: "You are running out of recovery codes. Generate new ones below."',
          '**Generate new codes** needs your current password and makes the old codes stop working. A wrong current password counts towards the sign-in lockout. Turning two-factor on or off signs your other devices out.',
          '**Turn off** is offered to staff who are not Super Admins. For a Super Admin the page says it cannot be turned off.',
        ],
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'If a Super Admin loses both their phone and their recovery codes, nobody can fix it from the admin. Keep recovery codes safe, and keep at least two Super Admin accounts, each with its own authenticator.',
      },
    ],
  },
  {
    id: 'staff-users',
    group: 'Access and security',
    title: 'Staff users',
    summary:
      'Look up staff accounts, create them, change their details and roles, and deactivate them. Needs the Users permissions, which by default only Super Admins hold.',
    screens: [
      { label: 'Staff users', path: '/users' },
      { label: 'New staff user', path: '/users/new' },
    ],
    blocks: [
      {
        kind: 'callout',
        tone: 'note',
        text: 'Permissions: **Users View** to read the list, **Users Manage** to create, change and deactivate. With the default roles only **Super Admins** hold either. The menu links to Staff users and Roles are shown to everyone, but anyone else who opens them gets a refusal such as "You are not allowed to do that."',
      },
      { kind: 'heading', text: 'Find people' },
      {
        kind: 'text',
        text: 'The list has **Search** (name or email), **Role** and **Status** (All, Active, Deactivated) filters with **Apply filters** and **Clear filters**, 25 per page. Columns: Name, Email, Roles, Status, **Last sign-in** (in UTC, not Colombo time; "Never" if they have not signed in) and the row buttons **Edit**, **Deactivate** or **Reactivate**. Someone who is not a Super Admin never sees Super Admin accounts in the list.',
      },
      { kind: 'heading', text: 'Create a staff user' },
      {
        kind: 'steps',
        items: [
          'Open **Staff users**, then press **New staff user**.',
          'Fill in **Email** (required, up to 256 characters, not used by any other account, staff or customer), **First name** (required, up to 80), **Last name** (optional) and **Initial password** (it must follow the password rule).',
          'Under **Roles**, tick **at least one** role. If you tick none, the form says "Choose at least one role for the new user." and sends nothing. A role you may not give is shown greyed out.',
          'Press **Create user**.',
        ],
      },
      {
        kind: 'text',
        text: 'There is no invitation email: whoever creates the account gives the new person the initial password themselves, and they must change it the first time they sign in. A Super Admin must also set up two-factor sign-in with an authenticator app on their phone before they can use the admin.',
      },
      { kind: 'heading', text: 'Change a person' },
      {
        kind: 'list',
        items: [
          '**Profile:** change First name and Last name and press **Save profile**. Only a Super Admin can change an email address.',
          '**Roles:** tick one box per role and press **Save roles**. It replaces the person\'s whole set of roles, and signs them out at once so the change applies. A role you may not give is shown greyed out. A user\'s permissions are the combination of all their roles.',
          '**Account status:** **Deactivate** stops them signing in, within about a minute. **Reactivate** brings them back. It happens at once with no confirmation. Staff accounts are never deleted, only deactivated. A **protected** account (one your developer has named in the server\'s settings, such as the support Super Admin) shows "Protected" instead of a Deactivate button, can never be deactivated by anyone, and its email address cannot be changed either. An account **managed by the technical owner** (for example the shop owner\'s own Super Admin login, set up that way by your developer) shows "Managed by the technical owner" instead of Deactivate or Reactivate: only the technical owner\'s protected account can switch it off or on or change its email address, and nobody else can, other Super Admins and the person themselves included. Their name and roles are still changed in the usual way. Only your developer can protect or guard an account.',
          'After saving a profile or roles the page shows no "saved" message. If nothing red appeared, it worked.',
          'There is no button to reset someone\'s password, unlock them, or reset their two-factor authentication.',
        ],
      },
      { kind: 'heading', text: 'The safety rules' },
      {
        kind: 'table',
        caption: 'What you cannot do, and the message you see',
        head: ['Rule', 'Message'],
        rows: [
          ['You cannot give a role that contains permissions you do not hold yourself.', '"That action grants or affects permissions you do not hold yourself."'],
          ['You cannot change, deactivate or reactivate someone whose permissions go beyond yours. If you are not a Super Admin, that includes every Super Admin. The same message is given if the person does not exist.', 'The same message as above.'],
          ['Nobody can change their own roles or deactivate their own account, Super Admins included.', '"You cannot change your own role assignments or deactivate your own account."'],
          ['A protected account cannot be deactivated, and its email address cannot be changed, by anyone, Super Admins included.', '"This account is protected and can never be deactivated." or "The email address of a protected account cannot be changed."'],
          ['An account managed by the technical owner can be activated, deactivated or given a new email address only by the technical owner\'s protected account.', '"Only the technical owner\'s account can activate or deactivate this account." or "Only the technical owner\'s account can change this account\'s email address."'],
          ['Only a Super Admin can change a staff email address.', '"Only a Super Admin can change a staff user\'s email address."'],
          ['There must always be at least one active Super Admin.', '"This would leave no active Super Admin. Assign the Super Admin role to another active user first."'],
          ['You need Users Manage to make changes.', '"You are not allowed to do that."'],
        ],
      },
      {
        kind: 'callout',
        tone: 'tip',
        text: 'Keep **two** Super Admin accounts (the shop owner and one other trusted person), each with two-factor authentication, so that one can act when the other is away. The system only enforces "at least one". At a monthly review, deactivate anyone who has left and check that both Super Admins can still sign in.',
      },
    ],
  },
  {
    id: 'staff-roles',
    group: 'Access and security',
    title: 'Roles and permissions: who can do what',
    summary:
      'The four roles, every permission and what it unlocks, and what a person sees when they lack one. Roles cannot be edited in the admin.',
    screens: [{ label: 'Roles', path: '/roles' }],
    blocks: [
      {
        kind: 'text',
        text: 'A **permission** is one thing a person may do, such as "Orders Edit". A **role** is a bundle of permissions. A person can hold several roles and gets all their permissions together. The **Roles** screen lists the roles and, when you press a row, the exact permissions in it.',
      },
      {
        kind: 'callout',
        tone: 'warning',
        text: 'The admin has **no way to create, edit or delete a role**, or to change what a role contains. The four roles below are fixed; only a software update changes them. If you need a different mix, give a person two roles, or ask your developer. A permission never implies another: "Edit" does not include "View". The role descriptions on the Roles screen mention internal document numbers; ignore them.',
      },
      { kind: 'heading', text: 'The four roles' },
      {
        kind: 'table',
        caption: 'The default roles',
        head: ['Role', 'Meant for', 'What it holds', 'What it lacks'],
        rows: [
          ['Super Admin', 'The shop owner and a trusted second person.', 'Everything, including bank and PayPal settings, the email account, and Staff users and Roles. Must use two-factor authentication.', 'Nothing.'],
          ['Store Manager', 'Day-to-day running of the shop.', '42 permissions: every module except Staff users and Roles, payment settings (bank, PayPal, payment methods) and the email account.', 'Users, Settings Payments, Settings Security.'],
          ['Order Staff', 'Looking after orders.', '6 permissions: the dashboard, view, edit and approve orders (bank transfers), view stock, view customers.', 'Products, categories, promotions, content, reports, settings, the audit log, refunds.'],
          ['Content Editor', 'Catalogue and shop content.', '20 permissions: products, categories, reviews, content and media.', 'The dashboard, orders, customers, inventory, promotions, reports, settings, the audit log.'],
        ],
      },
      { kind: 'heading', text: 'Every permission' },
      {
        kind: 'table',
        caption: 'The permission catalogue',
        head: ['Permission', 'What it lets you do', 'Held by (as well as Super Admin)'],
        rows: [
          ['Dashboard View', 'See the dashboard figures. Without it the page says "Your role does not include the dashboard figures."', 'Store Manager, Order Staff'],
          ['Orders View', 'See orders and invoices, the Bank transfers queue, payment slips and the courier list.', 'Store Manager, Order Staff'],
          ['Orders Edit', 'Move an order along: start processing, mark shipped with courier and tracking number, ready for collection, delivered or collected, correct the courier or tracking, cancel an unpaid order (with a reason) and edit the internal note.', 'Store Manager, Order Staff'],
          ['Orders Approve', 'Approve or reject a bank transfer and accept a late payment. Also lets you see and acknowledge payment alerts.', 'Store Manager, Order Staff'],
          ['Orders Refund', 'Meant for refunds. There is no refund action in the admin yet; today it only makes payment alerts visible.', 'Store Manager'],
          ['Products View, Create, Edit, Delete', 'See products, add one, change one and put it on or take it off the shop, remove one.', 'Store Manager, Content Editor'],
          ['Categories View, Create, Edit, Delete', 'See, add, change (also departments, reordering, hiding) and remove categories.', 'Store Manager, Content Editor'],
          ['Inventory View', 'See stock and its history.', 'Store Manager, Order Staff'],
          ['Inventory Edit', 'Change stock and low-stock thresholds.', 'Store Manager'],
          ['Customers View', 'See customers and their orders, and the Contact enquiries inbox.', 'Store Manager, Order Staff'],
          ['Customers Edit', 'Mark an enquiry Replied or Spam.', 'Store Manager'],
          ['Customers Delete', 'Delete an enquiry for good. Nothing else yet.', 'Store Manager'],
          ['Reviews View, Approve, Edit', 'See reviews; approve or show again; hide.', 'Store Manager, Content Editor'],
          ['Reviews Delete', 'Intended for deleting a review. Nothing uses it yet.', 'Store Manager, Content Editor'],
          ['Promotions View, Create, Edit, Delete', 'See, add, change and remove promo codes and sale campaigns.', 'Store Manager'],
          ['Content View, Create, Edit, Delete', 'The Homepage, Announcement bar, Mega menu cards, Size guides and Content pages screens.', 'Store Manager, Content Editor'],
          ['Media View, Create, Delete', 'Create lets you upload photographs and video from the forms. View and Delete are for look-ups and removals that no screen offers yet.', 'Store Manager, Content Editor'],
          ['Media Edit', 'Intended for editing picture descriptions. Nothing uses it yet.', 'Store Manager, Content Editor'],
          ['Reports View, Export', 'See the reports; download them as CSV files (recorded in the audit log).', 'Store Manager'],
          ['Traffic View', 'The Website traffic screen.', 'Store Manager'],
          ['Newsletter View, Export', 'See subscribers; download them as a CSV file (recorded in the audit log).', 'Store Manager'],
          ['Users View, Manage', 'Read the Staff users and Roles screens; create, change and deactivate staff.', 'Nobody by default: Super Admin only'],
          ['Settings View', 'Read Overseas delivery and Blocked countries. It does not open any settings form.', 'Store Manager'],
          ['Settings Edit', 'Edit the Logo, Contact and social, Invoice and fulfilment, Traffic, Product page texts and Reviews panels; add and change couriers; change Overseas delivery and Blocked countries.', 'Store Manager'],
          ['Settings Payments', 'Bank details, PayPal settings and exchange rate, and the payment-method switches.', 'Nobody by default: Super Admin only'],
          ['Settings Security', 'The email (SMTP) account. It can never be given to a role: Super Admin only.', 'Super Admin only'],
          ['Audit log View', 'Read the Audit log.', 'Store Manager'],
        ],
      },
      { kind: 'heading', text: 'What you see without a permission' },
      {
        kind: 'list',
        items: [
          'Only four menu links hide when you lack the permission: **Reports**, **Website traffic**, **Inventory** and **Reviews**. Every other link is shown to everyone. If you open a page you may not use, you see a sentence in place of the content: usually "You do not have permission to do that."; on the Audit log "You do not have permission to view the audit log."',
          'The Homepage, Announcement bar, Mega menu cards and Size guides screens say "Your role does not include (the screen). Ask an administrator for the Content permission."',
          'On many screens the buttons are still shown, and pressing one tells you "You do not have permission to do that." The server is the real guard, whatever the screen shows.',
          'The alert bell is not drawn at all for someone who may not see alerts.',
        ],
      },
    ],
  },
];
